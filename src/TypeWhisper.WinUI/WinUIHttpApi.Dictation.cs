using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class WinUIHttpApi
{
    private readonly LocalApiDictationSessions _dictations = new();
    private Task _dictationCompletion = Task.CompletedTask;
    private bool _startingDictation;

    private void RefreshApiDictation()
    {
        if (_startingDictation) return;
        _dictations.Refresh(session.ApiDictationGeneration, session.IsRecording, session.CanCancelProcessing,
            session.LastApiDictationRecordGeneration, session.LastApiDictationRecord);
    }

    private async Task<LocalApiResponse?> HandleDictationAsync(LocalApiRequest request, CancellationToken ct)
    {
        if (!request.Path.StartsWith("/v1/dictation/", StringComparison.Ordinal)) return null;
        if (request.Path is not ("/v1/dictation/status" or "/v1/dictation/transcription" or "/v1/dictation/start" or "/v1/dictation/stop")) return null;
        var read = request.Path is "/v1/dictation/status" or "/v1/dictation/transcription";
        if (request.Method != (read ? "GET" : "POST")) return Error(405, read ? "Use GET." : "Use POST.");
        if (request.Query.Keys.Any(key => request.Path != "/v1/dictation/transcription" || key != "id")) return Error(400, "Unknown query parameter.");
        if (request.Body.Length > 0 && request.Path != "/v1/dictation/start") return Error(400, "This endpoint accepts no body.");
        if (!_startingDictation) RefreshApiDictation();
        if (request.Path == "/v1/dictation/status")
            return LocalApiResponse.Json(200, new { state = session.IsRecording ? "recording" : session.CanCancelProcessing ? "processing" : "idle", is_recording = session.IsRecording, active_model = session.ActiveModelId });
        if (request.Path == "/v1/dictation/transcription")
        {
            if (!request.Query.TryGetValue("id", out var id) || !Guid.TryParse(id, out _)) return Error(400, "Missing or invalid id.");
            if (_dictations.Find(id!) is not { } item) return Error(404, "Dictation session not found.");
            return LocalApiResponse.Json(200, new { id = item.Id, status = item.Status, transcription = item.Transcription is { } record ? RecordPayload(record) : null, error = item.Error });
        }
        ct.ThrowIfCancellationRequested();
        if (request.Path == "/v1/dictation/start")
        {
            if (_startingDictation || !session.CanTranscribeFile || !_dictationCompletion.IsCompleted) return Error(409, "Finish the current operation before starting dictation.");
            AutomaticWorkflowSnapshot? workflow = null;
            if (request.Body.Length > 0)
            {
                using var json = JsonDocument.Parse(request.Body);
                if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Any(p => p.Name != "workflow_id") || json.RootElement.EnumerateObject().Count() > 1) return Error(400, "Expected an optional workflow_id.");
                if (json.RootElement.TryGetProperty("workflow_id", out var value))
                {
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return Error(400, "Invalid workflow_id.");
                    var selected = new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read().FirstOrDefault(w => string.Equals(w.Id, value.GetString(), StringComparison.OrdinalIgnoreCase));
                    if (selected is null) return Error(404, "Workflow not found.");
                    try { workflow = AutomaticWorkflowSnapshot.ForApi(session.WorkflowDefaults.Resolve(selected)); }
                    catch (InvalidOperationException ex) { return Error(409, ex.Message); }
                }
            }
            _startingDictation = true;
            try
            {
                if (workflow is null) await session.StartAsync(); else await session.StartAsync(workflow);
                if (!session.IsRecording) return Error(409, "Dictation could not start. Check the microphone and selected model.");
                var item = _dictations.Register(session.ApiDictationGeneration);
                return LocalApiResponse.Json(200, new { id = item.Id, status = "recording", workflow_id = workflow?.Id, workflow_name = workflow?.Name });
            }
            finally { _startingDictation = false; }
        }
        if (!session.IsRecording || _startingDictation || !_dictationCompletion.IsCompleted) return Error(409, "No dictation is recording.");
        var stopped = _dictations.Register(session.ApiDictationGeneration);
        _dictations.MarkProcessing(stopped.Id);
        _dictationCompletion = CompleteApiDictationAsync(stopped);
        return LocalApiResponse.Json(200, new { id = stopped.Id, status = "stopped" });
    }

    private async Task CompleteApiDictationAsync(LocalApiDictationSession item)
    {
        try { await session.StopAsync(); RefreshApiDictation(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _dictations.Fail(item.Id); }
    }

    private static object RecordPayload(TranscriptionRecord record) => new
    {
        text = record.DisplayText, raw_text = record.RawText, timestamp = record.Timestamp,
        app_name = record.AppName, app_bundle_id = (string?)null, app_url = record.AppUrl,
        duration = record.DurationSeconds, language = record.Language, engine = record.EngineUsed,
        model = record.ModelUsed, words_count = record.WordCount
    };
}
