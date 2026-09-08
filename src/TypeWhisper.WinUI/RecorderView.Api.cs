using System.Diagnostics;
using System.Text.Json.Serialization;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class RecorderView
{
    private sealed class ApiRecorderSession
    {
        public required string Id { get; init; }
        public string Status { get; set; } = "recording";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OutputFile { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; }
        [JsonIgnore]
        public bool Started { get; set; }
    }

    private readonly Dictionary<string, ApiRecorderSession> _apiRecorderSessions = new(StringComparer.OrdinalIgnoreCase);
    private ApiRecorderSession? _apiRecorderSession;
    private static readonly System.Text.Json.JsonSerializerOptions RecorderJson = new()
    { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };

    internal async Task<LocalApiResponse?> HandleApiAsync(LocalApiRequest request, CancellationToken ct)
    {
        if (request.Path is not ("/v1/recorder/start" or "/v1/recorder/stop" or "/v1/recorder/status" or "/v1/recorder/session")) return null;
        ct.ThrowIfCancellationRequested();
        if (_recorder is null || _libraryClosing) return RecorderApiError(503, "The recorder is unavailable.");
        var expectedMethod = request.Path is "/v1/recorder/start" or "/v1/recorder/stop" ? "POST" : "GET";
        if (request.Method != expectedMethod) return RecorderApiError(405, $"Use {expectedMethod}.");
        if (request.Body.Length != 0) return RecorderApiError(400, "This endpoint accepts query parameters only.");
        if (request.Path == "/v1/recorder/session")
        {
            if (request.Query.Count != 1 || !request.Query.TryGetValue("id", out var id) || !Guid.TryParse(id, out var guid))
                return RecorderApiError(400, "Provide a valid session id.");
            UpdateApiRecorderSession();
            return _apiRecorderSessions.TryGetValue(guid.ToString(), out var found)
                ? new LocalApiResponse(200, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(found, RecorderJson))
                : RecorderApiError(404, "Recorder session not found.");
        }
        if (request.Path == "/v1/recorder/start")
        {
            if (request.Query.Keys.Any(key => key is not ("mic" or "system_audio")))
                return RecorderApiError(400, "Unknown recorder parameter.");
            var preferences = _recorderPreferences?.Current ?? new RecorderPreferences();
            if (!TryRecorderSource(request, "mic", preferences.MicrophoneEnabled, out var microphone)
                || !TryRecorderSource(request, "system_audio", preferences.SystemAudioEnabled, out var systemAudio))
                return RecorderApiError(400, "Audio sources must be true, false, 1 or 0.");
            if (!microphone && !systemAudio) return RecorderApiError(400, "Enable at least one audio source.");
            if (_recorder.Busy || _recorder.State is RecorderState.Recording or RecorderState.Paused or RecorderState.Saving or RecorderState.SaveFailed)
                return RecorderApiError(409, "Finish or save the current recording first.");
            var started = NewApiRecorderSession();
            try
            {
                await StartRecordingAsync(preferences with { MicrophoneEnabled = microphone, SystemAudioEnabled = systemAudio });
                started.Started = true;
                Refresh();
                return LocalApiResponse.Json(200, new { id = started.Id, status = "recording" });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                started.Status = "failed";
                started.Error = "Could not start recording. Check the audio sources and active operations.";
                Trace.TraceError("API recorder start failed: {0}", ex);
                return RecorderApiError(ex is InvalidOperationException ? 409 : 500, started.Error);
            }
        }
        if (request.Query.Count != 0) return RecorderApiError(400, "This endpoint accepts no parameters.");
        if (request.Path == "/v1/recorder/status")
            return LocalApiResponse.Json(200, new { recording = _recorder.State is RecorderState.Recording or RecorderState.Paused });
        if (_recorder.Busy || _recorder.State is not (RecorderState.Recording or RecorderState.Paused))
            return RecorderApiError(409, "No recording is available to stop.");
        var stopped = _apiRecorderSession is { Started: true, Status: "recording" } current ? current : NewApiRecorderSession();
        stopped.Started = true;
        stopped.Status = "finalizing";
        _ = FinishApiRecordingAsync(stopped);
        return LocalApiResponse.Json(200, new { id = stopped.Id, status = "finalizing" });
    }

    private ApiRecorderSession NewApiRecorderSession()
    {
        // Bound in-memory polling history without removing the active session.
        if (_apiRecorderSessions.Count >= 100)
        {
            var oldest = _apiRecorderSessions.First(pair => pair.Value != _apiRecorderSession);
            _apiRecorderSessions.Remove(oldest.Key);
        }
        var created = new ApiRecorderSession { Id = Guid.NewGuid().ToString() };
        _apiRecorderSessions.Add(created.Id, created);
        return _apiRecorderSession = created;
    }

    private async Task FinishApiRecordingAsync(ApiRecorderSession stopped)
    {
        await StopAsync(); // Uses the same capture, WAV publication, reservation and retry path as the UI.
        UpdateApiRecorderSession();
        if (stopped.Status == "finalizing")
        {
            stopped.Status = "failed";
            stopped.Error = "Recording could not be saved. Check Recorder to retry.";
        }
    }

    private void UpdateApiRecorderSession()
    {
        if (_apiRecorderSession is not { Started: true } current || _recorder is null) return;
        if (_recorder.State == RecorderState.Saving) current.Status = "finalizing";
        if (_recorder.Busy) return;
        if (_recorder.State == RecorderState.Saved)
        {
            current.Status = "completed";
            current.OutputFile = _recorder.FilePath;
            current.Error = null;
        }
        else if (_recorder.Error is not null)
        {
            current.Status = "failed";
            current.Error = "Recording could not be completed. Check Recorder to retry.";
        }
    }

    private static bool TryRecorderSource(LocalApiRequest request, string key, bool fallback, out bool value)
    {
        value = fallback;
        if (!request.Query.TryGetValue(key, out var raw)) return true;
        if (raw is "true" or "1") { value = true; return true; }
        if (raw is "false" or "0") { value = false; return true; }
        return false;
    }

    private static LocalApiResponse RecorderApiError(int status, string message) => LocalApiResponse.Json(status, new
    {
        error = new
        {
            code = status switch { 400 => "bad_request", 404 => "not_found", 405 => "method_not_allowed", 409 => "conflict", 503 => "service_unavailable", _ => "error" },
            message
        }
    });
}
