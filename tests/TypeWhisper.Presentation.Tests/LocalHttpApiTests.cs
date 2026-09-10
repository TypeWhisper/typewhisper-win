using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalHttpApiTests
{
    private const string Token = "a7d18284e6504fe2a1cc070c62850709";

    [Fact]
    public async Task CustomStatusIsPublicButCannotBypassOriginOrProtectedEndpointChecks()
    {
        var statusCalls = 0;
        var backendCalls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            backendCalls++;
            return Task.FromResult(LocalApiResponse.Json(200, new { }));
        }, statusHandler: ct =>
        {
            Assert.True(ct.CanBeCanceled);
            statusCalls++;
            return Task.FromResult(LocalApiResponse.Json(200, new { status = "ready", model = "selected-model" }));
        });
        await server.StartAsync();
        using var client = Client(server);
        Assert.Contains("selected-model", await client.GetStringAsync("v1/status"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("v1/status", new StringContent(""))).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:8978");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("v1/status")).StatusCode);
        Assert.Equal(1, statusCalls);
        Assert.Equal(0, backendCalls);
    }

    [Fact]
    public async Task CustomStatusDoesNotWaitForDeclaredRequestBody()
    {
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => throw new InvalidOperationException(),
            statusHandler: _ => Task.FromResult(LocalApiResponse.Json(200, new { status = "ready" })));
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /v1/status HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 999999999\r\n\r\n"));
        using var reader = new StreamReader(tcp.GetStream());
        Assert.Contains("200", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AfterResponseRunsOnceAfterAcceptedResponseIsWritten()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var failures = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => Task.FromResult(
            LocalApiResponse.Json(202, new { status = "restoring" }) with
            {
                AfterResponse = () => { Interlocked.Increment(ref callbacks); completed.TrySetResult(); },
                ResponseFailed = () => Interlocked.Increment(ref failures)
            }));
        await server.StartAsync();
        using var client = Client(server, true);
        using var response = await client.PostAsync("v1/settings/import", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("restoring", await response.Content.ReadAsStringAsync());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync();
        Assert.Equal(1, callbacks);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task FailedResponseWriteDoesNotRunAfterResponse()
    {
        var callbacks = 0;
        var failures = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => Task.FromResult(
            // Invalid HTTP status forces response publication to fail before any bytes are sent.
            LocalApiResponse.Json(99, new { }) with
            {
                AfterResponse = () => Interlocked.Increment(ref callbacks),
                ResponseFailed = () => Interlocked.Increment(ref failures)
            }));
        await server.StartAsync();
        using var client = Client(server, true);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("v1/models")).StatusCode);
        await server.StopAsync();
        Assert.Equal(0, callbacks);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task TimedOutResponseDoesNotRunAfterResponse()
    {
        var callbacks = 0;
        var failures = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, async (_, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { }
            return LocalApiResponse.Json(202, new { }) with
            {
                AfterResponse = () => Interlocked.Increment(ref callbacks),
                ResponseFailed = () => Interlocked.Increment(ref failures)
            };
        }, requestTimeout: TimeSpan.FromMilliseconds(100));
        await server.StartAsync();
        using var client = Client(server, true);
        using var response = await client.GetAsync("v1/models");
        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        await server.StopAsync();
        Assert.Equal(0, callbacks);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task FailedCompletionCallbackReleasesReservationAndCleanupCannotFaultTheHost()
    {
        var failures = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => Task.FromResult(
            LocalApiResponse.Json(202, new { }) with
            {
                AfterResponse = () => throw new InvalidOperationException("Scheduling failed."),
                ResponseFailed = () => { Interlocked.Increment(ref failures); throw new InvalidOperationException("Cleanup failed."); }
            }));
        await server.StartAsync();
        using var client = Client(server, true);
        using var response = await client.GetAsync("v1/models");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await server.StopAsync();
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenRequirementControlsLegacyRequestsButAlwaysBlocksBrowserOrigins(bool requireAuthentication)
    {
        var calls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(LocalApiResponse.Json(200, new { }));
        }, requireAuthentication: requireAuthentication);
        await server.StartAsync();
        using var client = Client(server);
        var expected = requireAuthentication ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
        Assert.Equal(expected, (await client.GetAsync("v1/models")).StatusCode);
        Assert.Equal(expected, (await client.PostAsync("v1/transcribe", new StringContent("audio"))).StatusCode);
        Assert.Equal(requireAuthentication ? 0 : 2, calls);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("v1/models")).StatusCode);
        var authenticatedCalls = calls;
        foreach (var origin in new[] { "https://example.com", "http://localhost:8978", "null" })
        {
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", origin);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("v1/models")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("v1/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("docs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("v1/transcribe", new StringContent("audio"))).StatusCode);
        }
        Assert.Equal(authenticatedCalls, calls);
    }

    [Fact]
    public async Task DocumentationIsPublicStaticAndDoesNotExposeCredentialsOrBypassApiAuthentication()
    {
        var calls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            calls++;
            return Task.FromResult(LocalApiResponse.Json(200, new { }));
        });
        await server.StartAsync();
        using var client = Client(server);
        foreach (var path in new[] { "docs", "docs/" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains($"http://127.0.0.1:{server.Port}/v1/transcribe", html);
            Assert.Contains("/v1/transcribe/local-file", html);
            Assert.Contains("api-discovery.json", html);
            Assert.Contains("C:/Audio/sample.wav", html);
            Assert.DoesNotContain("{{PORT}}", html);
            Assert.DoesNotContain(Token, html);
            Assert.DoesNotContain("<script", html);
            Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("docs/private")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("docs", new StringContent(""))).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("docs")).StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StatusIsMinimalAndAuthenticationPrecedesBackend()
    {
        var calls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(LocalApiResponse.Json(200, new { secret = "private" }));
        });
        await server.StartAsync();
        using var client = Client(server);
        var status = await client.GetStringAsync("v1/status");
        Assert.Equal("{\"status\":\"ok\",\"api_version\":\"1.1\"}", status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("v1/models")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "incorrect");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("v1/status", new StringContent("x"))).StatusCode);
        Assert.Equal(0, calls);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("v1/models")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("X-TypeWhisper-API-Token", Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("v1/models")).StatusCode);
        Assert.Equal(2, calls);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:9999");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("v1/status")).StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RoutesMethodBodyContentTypeAndQueryWithoutChangingBackendResponse()
    {
        LocalApiRequest? captured = null;
        await using var server = new LocalHttpApi(FreePort(), Token, (request, _) =>
        {
            captured = request;
            return Task.FromResult(LocalApiResponse.Json(405, new { error = "Method not allowed" }));
        });
        await server.StartAsync();
        using var client = Client(server, true);
        var response = await client.PutAsync("v1/example?language=de&prompt=hello%20world", new StringContent("payload", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("PUT", captured!.Method);
        Assert.Equal("/v1/example", captured.Path);
        Assert.Equal("payload", Encoding.UTF8.GetString(captured.Body));
        Assert.StartsWith("text/plain", captured.ContentType);
        Assert.Equal("hello world", captured.Query["prompt"]);
        Assert.Equal("de", captured.Query["language"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOversizedBodiesIncludingChunked(bool chunked)
    {
        var calls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            calls++;
            return Task.FromResult(LocalApiResponse.Json(200, new { }));
        }, maxBodyBytes: 16);
        await server.StartAsync();
        using var client = Client(server, true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/transcribe") { Content = new ByteArrayContent(new byte[17]) };
        request.Headers.TransferEncodingChunked = chunked;
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RejectsExcessConcurrencyAndDrainsBackendOnShutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalHttpApi(FreePort(), Token, async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { canceled.SetResult(); }
            return LocalApiResponse.Json(200, new { });
        }, maxConcurrency: 1);
        await server.StartAsync();
        using var client = Client(server, true);
        var active = client.GetAsync("v1/transcribe");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("v1/models")).StatusCode);
        using var publicClient = Client(server);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await publicClient.GetAsync("v1/status")).StatusCode);
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(canceled.Task.IsCompleted);
        Assert.False(server.IsRunning);
        try { using var response = await active; } catch (HttpRequestException) { }
    }

    [Fact]
    public async Task RequestTimeoutCancelsBackendAndReturnsGenericError()
    {
        await using var server = new LocalHttpApi(FreePort(), Token, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return LocalApiResponse.Json(200, new { });
        }, requestTimeout: TimeSpan.FromMilliseconds(100));
        await server.StartAsync();
        using var client = Client(server, true);
        Assert.Equal(HttpStatusCode.RequestTimeout, (await client.GetAsync("v1/models")).StatusCode);
    }

    [Fact]
    public async Task ShutdownInterruptsSlowIncompleteUpload()
    {
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => throw new InvalidOperationException("Backend must not run"));
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST /v1/transcribe HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nAuthorization: Bearer {Token}\r\nContent-Length: 100\r\n\r\nx"));
        using var client = Client(server);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("v1/status")).StatusCode);
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task UnauthorizedIncompleteBodyIsRejectedWithoutWaitingForUpload()
    {
        var calls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            calls++;
            return Task.FromResult(LocalApiResponse.Json(200, new { }));
        });
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST /v1/transcribe HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 999999999\r\n\r\n"));
        using var reader = new StreamReader(tcp.GetStream());
        var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("401", status);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("language=en&language=de")]
    [InlineData("language=en&LANGUAGE=de")]
    [InlineData("unnamed")]
    [InlineData("=value")]
    public async Task AmbiguousOrUnnamedQueryParametersAreRejected(string query)
    {
        var calls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            calls++;
            return Task.FromResult(LocalApiResponse.Json(200, new { }));
        });
        await server.StartAsync();
        using var client = Client(server, true);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"v1/transcribe?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"v1/status?{query}")).StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RequestTimeoutInterruptsIncompleteUploadBeforeBackend()
    {
        var calls = 0;
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) =>
        {
            calls++;
            return Task.FromResult(LocalApiResponse.Json(200, new { }));
        }, requestTimeout: TimeSpan.FromMilliseconds(100));
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST /v1/transcribe HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nAuthorization: Bearer {Token}\r\nContent-Length: 100\r\n\r\nx"));
        using var reader = new StreamReader(tcp.GetStream());
        Assert.Contains("408", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PublicStatusDoesNotBufferDeclaredRequestBody()
    {
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => throw new InvalidOperationException());
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /v1/status HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 999999999\r\n\r\n"));
        using var reader = new StreamReader(tcp.GetStream());
        Assert.Contains("200", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LocalhostPrefixAcceptsStatusRequests()
    {
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => throw new InvalidOperationException());
        await server.StartAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"http://localhost:{server.Port}/v1/status")).StatusCode);
    }

    [Fact]
    public async Task PortCollisionFailsCleanlyAndStoppedInstanceRestarts()
    {
        var port = FreePort();
        static Task<LocalApiResponse> Handler(LocalApiRequest _, CancellationToken __) => Task.FromResult(LocalApiResponse.Json(404, new { }));
        await using var first = new LocalHttpApi(port, Token, Handler);
        await using var second = new LocalHttpApi(port, Token, Handler);
        await first.StartAsync();
        await Assert.ThrowsAsync<HttpListenerException>(() => second.StartAsync());
        Assert.False(second.IsRunning);
        await first.StopAsync();
        await second.StartAsync();
        using var client = Client(second, true);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("v1/missing")).StatusCode);
        await second.StopAsync();
        await second.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("v1/status")).StatusCode);
    }

    [Fact]
    public async Task BackendExceptionsDoNotExposeSensitiveDetails()
    {
        await using var server = new LocalHttpApi(FreePort(), Token, (_, _) => throw new InvalidOperationException("secret transcript"));
        await server.StartAsync();
        using var client = Client(server, true);
        var response = await client.GetAsync("v1/models");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("                                ")]
    public void WeakTokensAreRejected(string token) => Assert.Throws<ArgumentException>(() =>
        new LocalHttpApi(8978, token, (_, _) => Task.FromResult(LocalApiResponse.Json(200, new { }))));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpClient Client(LocalHttpApi server, bool authenticated = false)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/"), Timeout = TimeSpan.FromSeconds(10) };
        if (authenticated) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }
}
