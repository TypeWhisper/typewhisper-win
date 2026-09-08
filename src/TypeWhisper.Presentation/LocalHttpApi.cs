using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>An authenticated request whose body has passed the transport size limit.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Absolute URL path.</param>
/// <param name="Body">Buffered request body.</param>
/// <param name="ContentType">Declared body media type.</param>
/// <param name="Query">Decoded query parameters.</param>
public sealed record LocalApiRequest(string Method, string Path, byte[] Body, string? ContentType,
    IReadOnlyDictionary<string, string?> Query);

/// <summary>A backend response ready to send over HTTP.</summary>
/// <param name="StatusCode">HTTP response status.</param>
/// <param name="Body">Encoded response bytes.</param>
/// <param name="ContentType">Response media type.</param>
public sealed record LocalApiResponse(int StatusCode, byte[] Body, string ContentType = "application/json")
{
    /// <summary>Serializes a JSON response as UTF-8.</summary>
    public static LocalApiResponse Json(int statusCode, object value) =>
        new(statusCode, JsonSerializer.SerializeToUtf8Bytes(value));
}

/// <summary>A loopback-only transport. Backend operations must honor their cancellation token.</summary>
public sealed class LocalHttpApi : IAsyncDisposable
{
    private readonly Func<LocalApiRequest, CancellationToken, Task<LocalApiResponse>> _handler;
    private readonly byte[] _tokenHash;
    private readonly int _maxBodyBytes;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _requestsLock = new();
    private readonly HashSet<Task> _requests = [];
    private HttpListener? _listener;
    private CancellationTokenSource? _stopping;
    private Task? _acceptLoop;

    /// <summary>Creates a host with a required token and bounded request admission.</summary>
    public LocalHttpApi(int port, string token,
        Func<LocalApiRequest, CancellationToken, Task<LocalApiResponse>> handler,
        int maxBodyBytes = 32 * 1024 * 1024, int maxConcurrency = 4, TimeSpan? requestTimeout = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("A token with at least 32 non-whitespace characters is required.", nameof(token));
        if (maxBodyBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        Port = port;
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _maxBodyBytes = maxBodyBytes;
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>The configured loopback port.</summary>
    public int Port { get; }
    /// <summary>Whether the listener currently accepts connections.</summary>
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Binds the configured port, reporting binding failures to the caller.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Prefixes.Add($"http://localhost:{Port}/");
            try { listener.Start(); }
            catch { listener.Close(); throw; }
            _listener = listener;
            _stopping = new CancellationTokenSource();
            _acceptLoop = AcceptAsync(listener, _stopping.Token);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Stops admission, cancels active requests, and drains all backend work.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listener is null) return;
            // Once shutdown begins, finish draining even if the caller stops waiting.
            _stopping!.Cancel();
            _listener.Close();
            if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
            Task[] requests;
            lock (_requestsLock) requests = _requests.ToArray();
            await Task.WhenAll(requests).ConfigureAwait(false);
            _listener = null;
            _acceptLoop = null;
            _stopping.Dispose();
            _stopping = null;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Stops and drains the host.</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptAsync(HttpListener listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            { break; }
            var admitted = _slots.Wait(0);
            if (!admitted)
            {
                // Do not create an unbounded task queue while clients exceed admission capacity.
                await ProcessAsync(context, false, stopping).ConfigureAwait(false);
                continue;
            }
            var task = ProcessAsync(context, admitted, stopping);
            lock (_requestsLock) _requests.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_requestsLock) _requests.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ProcessAsync(HttpListenerContext context, bool admitted, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            LocalApiResponse response;
            var request = context.Request;
            var publicStatus = request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/v1/status";
            if (request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) || request.Headers["Origin"] is not null)
                response = Error(403, "Request origin is not allowed.");
            else if (!publicStatus && !Authenticated(request))
                response = Error(401, "Authentication required.");
            else if (!admitted)
                response = Error(429, "Too many requests.");
            else if (request.QueryString.AllKeys.Any(key => string.IsNullOrEmpty(key) || request.QueryString.GetValues(key)?.Length != 1))
                response = Error(400, "Query parameters must have unique, non-empty names.");
            else if (publicStatus)
                response = LocalApiResponse.Json(200, new { status = "ok", api_version = "1.1" });
            else if (request.ContentLength64 > _maxBodyBytes)
                response = Error(413, "Request body is too large.");
            else
            {
                using var body = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    // HttpListener streams on Windows may ignore cancellation. Bound the wait;
                    // closing the response in finally also terminates the outstanding transport I/O.
                    var count = await request.InputStream.ReadAsync(buffer.AsMemory(0,
                        (int)Math.Min(buffer.Length, _maxBodyBytes - body.Length + 1)), timeout.Token)
                        .AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (body.Length + count > _maxBodyBytes)
                    {
                        await WriteAsync(context, Error(413, "Request body is too large."), timeout.Token).ConfigureAwait(false);
                        return;
                    }
                    body.Write(buffer, 0, count);
                }
                timeout.Token.ThrowIfCancellationRequested();
                var query = request.QueryString.AllKeys
                    .ToDictionary(key => key!, key => request.QueryString[key], StringComparer.Ordinal);
                response = await _handler(new LocalApiRequest(request.HttpMethod, request.Url!.AbsolutePath,
                    body.ToArray(), request.ContentType, query), timeout.Token).ConfigureAwait(false);
            }
            await WriteAsync(context, response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!stopping.IsCancellationRequested) await TryWriteErrorAsync(context, 408, "Request timed out.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await TryWriteErrorAsync(context, 500, "Request failed.").ConfigureAwait(false);
        }
        finally
        {
            try { context.Response.Close(); } catch { /* Client disconnected or listener stopped. */ }
            if (admitted) _slots.Release();
        }
    }

    private bool Authenticated(HttpListenerRequest request)
    {
        var authorization = request.Headers["Authorization"];
        var bearer = authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? authorization[7..] : null;
        // Evaluate both checks, and compare fixed-size digests rather than variable-length secrets.
        return TokenMatches(bearer) | TokenMatches(request.Headers["X-TypeWhisper-API-Token"]);
    }

    private bool TokenMatches(string? candidate) => CryptographicOperations.FixedTimeEquals(
        _tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? string.Empty)));

    private static LocalApiResponse Error(int status, string message) => LocalApiResponse.Json(status, new { error = message });

    private static async Task TryWriteErrorAsync(HttpListenerContext context, int status, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await WriteAsync(context, Error(status, message), timeout.Token).ConfigureAwait(false); }
        catch { /* No response can be delivered to a disconnected client. */ }
    }

    private static async Task WriteAsync(HttpListenerContext context, LocalApiResponse response, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength64 = response.Body.Length;
        context.Response.KeepAlive = false;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(response.Body, cancellationToken)
            .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
