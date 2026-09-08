using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class WinUIHttpApi(LocalDictationSession session, DispatcherQueue dispatcher)
{
    private sealed record Preferences(bool Enabled = false, int Port = 8978);
    private readonly WindowsPluginSecretStore _secrets = new(WinUIProfile.DataPath("HttpApi"));
    private readonly SemaphoreSlim _changes = new(1, 1);
    private LocalHttpApi? _host;
    private string? _token;
    private bool _closed;
    private Preferences _preferences = new();
    internal bool Enabled => _preferences.Enabled;
    internal int Port => _preferences.Port;
    internal bool Running => _host?.IsRunning == true;
    internal string Status { get; private set; } = "HTTP API is off.";
    internal event Action? Changed;
    private static string SettingsPath => WinUIProfile.DataPath("http-api.json");
    private static string PortPath => WinUIProfile.DataPath("api-port");
    private static string DiscoveryPath => WinUIProfile.DataPath("api-discovery.json");

    internal async Task InitializeAsync()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                if (new FileInfo(SettingsPath).Length > 4096) throw new IOException();
                _preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) ?? new();
                if (_preferences.Port is < 1024 or > 65535) throw new IOException();
            }
            if (_preferences.Enabled) await ConfigureAsync(true, Port);
            else RemoveDiscovery();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { RemoveDiscovery(); Status = "HTTP API settings could not be loaded. The server is off."; Changed?.Invoke(); }
    }

    internal async Task ConfigureAsync(bool enabled, int port)
    {
        if (_closed) return;
        if (port is < 1024 or > 65535) { Status = "Choose a port from 1024 to 65535."; Changed?.Invoke(); return; }
        await _changes.WaitAsync();
        try
        {
            if (_closed) return;
            if (_host is not null) { await _host.StopAsync(); _host = null; }
            RemoveDiscovery();
            _preferences = new(enabled, port);
            Directory.CreateDirectory(WinUIProfile.Root);
            File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(_preferences));
            File.Move(SettingsPath + ".tmp", SettingsPath, true);
            if (!enabled) { Status = "HTTP API is off."; return; }
            _token ??= await _secrets.LoadAsync("token");
            if (_token is null)
            {
                _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await _secrets.StoreAsync("token", _token);
            }
            if (_closed) return;
            _host = new LocalHttpApi(port, _token, DispatchAsync);
            await _host.StartAsync();
            var discovery = new FileInfo(DiscoveryPath + ".tmp");
            using (discovery.Create()) { }
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            discovery.SetAccessControl(security);
            File.WriteAllText(discovery.FullName, JsonSerializer.Serialize(new
            {
                version = 1, token = _token,
                host = "127.0.0.1", port, base_url = $"http://127.0.0.1:{port}", api_version = "1.1",
                pid = Environment.ProcessId, requires_authentication = true
            }));
            File.Move(DiscoveryPath + ".tmp", DiscoveryPath, true);
            File.WriteAllText(PortPath + ".tmp", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(PortPath + ".tmp", PortPath, true);
            Status = $"Listening on http://127.0.0.1:{port}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_host is not null) { await _host.StopAsync(); _host = null; }
            RemoveDiscovery();
            Status = "HTTP API could not start or save its settings. Check the port and profile access, then retry.";
            System.Diagnostics.Debug.WriteLine("HTTP API configuration: " + ex.GetType().Name);
        }
        finally { _changes.Release(); Changed?.Invoke(); }
    }

    internal string? TokenForCopy => Running ? _token : null;
    internal Task ShutdownAsync()
    {
        _closed = true;
        // Stop admission and request cancellation before waiting for any concurrent configuration.
        var stop = _host?.StopAsync() ?? Task.CompletedTask;
        return FinishShutdownAsync(stop);
    }
    private async Task FinishShutdownAsync(Task stop)
    {
        await stop;
        await _changes.WaitAsync();
        try
        {
            if (_host is not null) { await _host.StopAsync(); _host = null; }
            RemoveDiscovery();
        }
        finally { _changes.Release(); }
    }
    private static void RemoveDiscovery()
    {
        try { File.Delete(DiscoveryPath); File.Delete(PortPath); File.Delete(DiscoveryPath + ".tmp"); File.Delete(PortPath + ".tmp"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Debug.WriteLine("API discovery cleanup failed: " + ex.GetType().Name); }
    }

    private Task<LocalApiResponse> DispatchAsync(LocalApiRequest request, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<LocalApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (_closed) { completion.TrySetResult(Error(503, "The app is shutting down.")); return; }
                completion.TrySetResult(await HandleAsync(request, ct));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (LocalApiRequestException ex) { completion.TrySetResult(Error(ex.StatusCode, ex.Message)); }
            catch (NotSupportedException) { completion.TrySetResult(Error(422, "This model or file does not support the requested operation.")); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine("API processing failed: " + ex.GetType().Name);
                completion.TrySetResult(Error(500, "Processing failed. Check the model and audio file, then retry."));
            }
        })) completion.TrySetResult(Error(503, "The app is unavailable."));
        return completion.Task;
    }
    private static LocalApiResponse Error(int code, string message) => LocalApiResponse.Json(code, new { error = message });

    private async Task<LocalApiResponse> HandleAsync(LocalApiRequest request, CancellationToken ct)
    {
        if (request.Path == "/v1/models")
        {
            if (request.Method != "GET") return Error(405, "Use GET.");
            if (request.Query.Count > 0 || request.Body.Length > 0) return Error(400, "This endpoint accepts no parameters.");
            return LocalApiResponse.Json(200, new
            {
                status = session.IsReady ? session.CanTranscribeFile ? "ready" : "busy" : "no_model",
                engine = session.ActiveEngineId, model = session.ActiveModelId,
                models = session.DictationProviders.SelectMany(provider => provider.Models.Select(model => new
                {
                    id = model.Id, full_id = provider.Id + ":" + model.Id, engine = provider.Id, name = model.Name,
                    status = !provider.Enabled ? "disabled" : !provider.Configured ? "not_configured" : model.Ready ? "ready" : "not_downloaded",
                    active = provider.Id == session.ActiveProviderId && model.Id == session.ActiveModelId,
                    cloud = provider.Cloud
                })).ToArray()
            });
        }
        if (request.Path == "/v1/capabilities")
            return request.Method == "GET" ? LocalApiResponse.Json(200, new
            {
                api_version = "1.1", endpoints = new[] { "/v1/status", "/v1/models", "/v1/capabilities", "/v1/transcribe", "/v1/transcribe/local-file" },
                response_formats = new[] { "json", "text", "srt", "vtt" }, max_upload_bytes = 32 * 1024 * 1024,
                model_selection = "current model; engine/model parameters validate selection",
                saves_history = false, supports_dictation_control = false
            }) : Error(405, "Use GET.");
        if (request.Path is not ("/v1/transcribe" or "/v1/transcribe/local-file")) return Error(404, "Not found.");
        if (request.Method != "POST") return Error(405, "Use POST.");
        var parsed = LocalApiTranscription.Parse(request);
        if (!session.IsReady) return Error(503, "Select a ready transcription model in Dictation.");
        if (!session.CanTranscribeFile) return Error(409, "The transcription engine is busy.");
        string? temporary = null;
        try
        {
            var path = parsed.LocalPath;
            if (path is not null)
            {
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\", StringComparison.Ordinal))
                    return Error(400, "Use an absolute local file path.");
                // Inspect from the root before accessing a descendant of any junction.
                var fullPath = Path.GetFullPath(path);
                var current = Path.GetPathRoot(fullPath)!;
                if (new DriveInfo(current).DriveType == DriveType.Network) return Error(400, "Use a local drive.");
                try
                {
                    foreach (var component in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                    {
                        current = Path.Combine(current, component);
                        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                            return Error(400, "Linked file paths are not supported.");
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                { return Error(404, "The audio file does not exist."); }
                if (!File.Exists(path)) return Error(404, "The audio file does not exist.");
            }
            else
            {
                var directory = WinUIProfile.DataPath("HttpApi", "Uploads");
                Directory.CreateDirectory(directory);
                var extension = Path.GetExtension(parsed.FileName ?? "audio.wav");
                if (extension.Length > 10 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) return Error(400, "Invalid audio filename.");
                path = temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
                await File.WriteAllBytesAsync(path, parsed.Audio.ToArray(), ct);
            }
            ct.ThrowIfCancellationRequested();
            if (_closed) return Error(503, "The app is shutting down.");
            if (!session.CanTranscribeFile) return Error(409, "The transcription engine is busy.");
            var result = await session.TranscribeFileAsync(path, _ => { }, ct, parsed);
            ct.ThrowIfCancellationRequested();
            if (parsed.ResponseFormat == "json")
                return LocalApiResponse.Json(200, new { text = result.Text, engine = result.Provider, model = result.Model,
                    duration = result.Duration, warnings = result.Warning, segments = result.Segments.Select(segment => new { text = segment.Text, start = segment.Start, end = segment.End }) });
            return LocalApiTranscription.FormatResponse(result.Text,
                result.Segments.Select(segment => new LocalApiTranscriptSegment(segment.Text, segment.Start, segment.End)), parsed.ResponseFormat);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Debug.WriteLine("API upload cleanup failed: " + ex.GetType().Name); }
            }
        }
    }
}
