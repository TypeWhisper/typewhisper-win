using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Services;

public sealed class HttpApiService : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IHistoryService _history;
    private readonly IProfileService _profiles;
    private readonly DictationViewModel _dictation;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public bool IsRunning => _listener?.IsListening == true;

    public HttpApiService(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IHistoryService history,
        IProfileService profiles,
        DictationViewModel dictation)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _history = history;
        _profiles = profiles;
        _dictation = dictation;
    }

    public void Start(int port)
    {
        if (_listener is { IsListening: true }) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* continue listening */ }
        }
    }

    private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            var (statusCode, body) = (path, method) switch
            {
                ("/v1/status", "GET") => HandleStatus(),
                ("/v1/models", "GET") => HandleModels(),
                ("/v1/transcribe", "POST") => await HandleTranscribe(request, ct),
                ("/v1/history", "GET") => HandleHistorySearch(request),
                ("/v1/history", "DELETE") => HandleHistoryDelete(request),
                ("/v1/profiles", "GET") => HandleProfilesList(),
                ("/v1/profiles/toggle", "PUT") => HandleProfileToggle(request),
                ("/v1/dictation/start", "POST") => await HandleDictationStart(),
                ("/v1/dictation/stop", "POST") => await HandleDictationStop(),
                ("/v1/dictation/status", "GET") => HandleDictationStatus(),
                _ => (404, JsonSerializer.Serialize(new { error = "Not found" }))
            };

            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            response.ContentType = "application/json";
            var errorBytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { error = ex.Message }));
            response.ContentLength64 = errorBytes.Length;
            await response.OutputStream.WriteAsync(errorBytes, ct);
        }
        finally
        {
            response.Close();
        }
    }

    private (int, string) HandleStatus()
    {
        var activePlugin = _modelManager.ActiveTranscriptionPlugin;
        var result = new
        {
            status = _modelManager.Engine.IsModelLoaded ? "ready" : "no_model",
            activeModel = _modelManager.ActiveModelId,
            apiVersion = "1.0",
            supports_streaming = activePlugin?.SupportsStreaming ?? false,
            supports_translation = activePlugin?.SupportsTranslation ?? false
        };
        return (200, JsonSerializer.Serialize(result));
    }

    private (int, string) HandleModels()
    {
        var models = _modelManager.PluginManager.TranscriptionEngines
            .SelectMany(e => e.TranscriptionModels.Select(m =>
            {
                var fullId = ModelManagerService.GetPluginModelId(e.PluginId, m.Id);
                return new
                {
                    id = fullId,
                    name = $"{e.ProviderDisplayName}: {m.DisplayName}",
                    size = m.SizeDescription ?? (e.SupportsModelDownload ? "Local" : "Cloud"),
                    engine = e.PluginId,
                    downloaded = _modelManager.IsDownloaded(fullId),
                    active = _modelManager.ActiveModelId == fullId
                };
            }));
        return (200, JsonSerializer.Serialize(new { models }));
    }

    private async Task<(int, string)> HandleTranscribe(HttpListenerRequest request, CancellationToken ct)
    {
        if (!_modelManager.Engine.IsModelLoaded)
            return (503, JsonSerializer.Serialize(new { error = "No model loaded" }));

        var tempPath = Path.Combine(Path.GetTempPath(), $"tw_api_{Guid.NewGuid()}.tmp");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await request.InputStream.CopyToAsync(fs, ct);
            }

            var samples = await _audioFile.LoadAudioAsync(tempPath, ct);

            var s = _settings.Current;
            var language = request.QueryString["language"] ?? (s.Language == "auto" ? null : s.Language);
            var taskStr = request.QueryString["task"] ?? s.TranscriptionTask;
            var task = taskStr == "translate" ? TranscriptionTask.Translate : TranscriptionTask.Transcribe;
            var responseFormat = request.QueryString["response_format"] ?? "json";

            var result = await _modelManager.Engine.TranscribeAsync(samples, language, task, ct);

            var response = new
            {
                text = result.Text,
                language = result.DetectedLanguage,
                duration = result.Duration,
                processing_time = result.ProcessingTime,
                segments = result.Segments.Select(seg => new
                {
                    text = seg.Text,
                    start = seg.Start,
                    end = seg.End
                })
            };

            return (200, JsonSerializer.Serialize(response));
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // GET /v1/history?q=&limit=&offset=
    private (int, string) HandleHistorySearch(HttpListenerRequest request)
    {
        var query = request.QueryString["q"] ?? "";
        var limitStr = request.QueryString["limit"];
        var offsetStr = request.QueryString["offset"];

        var limit = int.TryParse(limitStr, out var l) ? l : 50;
        var offset = int.TryParse(offsetStr, out var o) ? o : 0;

        var records = string.IsNullOrWhiteSpace(query)
            ? _history.Records
            : _history.Search(query);

        var paged = records.Skip(offset).Take(limit).Select(r => new
        {
            id = r.Id,
            timestamp = r.Timestamp.ToString("o"),
            text = r.FinalText,
            raw_text = r.RawText,
            app = r.AppProcessName,
            duration = r.DurationSeconds,
            language = r.Language,
            engine = r.EngineUsed,
            model = r.ModelUsed,
            profile = r.ProfileName,
            words = r.WordCount
        });

        var result = new
        {
            total = records.Count,
            offset,
            limit,
            records = paged
        };
        return (200, JsonSerializer.Serialize(result));
    }

    // DELETE /v1/history?id=
    private (int, string) HandleHistoryDelete(HttpListenerRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrEmpty(id))
            return (400, JsonSerializer.Serialize(new { error = "Missing id parameter" }));

        _history.DeleteRecord(id);
        return (200, JsonSerializer.Serialize(new { deleted = true, id }));
    }

    // GET /v1/profiles
    private (int, string) HandleProfilesList()
    {
        var profiles = _profiles.Profiles.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            is_enabled = p.IsEnabled,
            priority = p.Priority,
            process_names = p.ProcessNames,
            url_patterns = p.UrlPatterns,
            input_language = p.InputLanguage,
            translation_target = p.TranslationTarget,
            selected_task = p.SelectedTask,
            model_override = p.TranscriptionModelOverride,
            prompt_action_id = p.PromptActionId
        });

        return (200, JsonSerializer.Serialize(new { profiles }));
    }

    // PUT /v1/profiles/toggle?id=
    private (int, string) HandleProfileToggle(HttpListenerRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrEmpty(id))
            return (400, JsonSerializer.Serialize(new { error = "Missing id parameter" }));

        var profile = _profiles.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
            return (404, JsonSerializer.Serialize(new { error = "Profile not found" }));

        _profiles.UpdateProfile(profile with { IsEnabled = !profile.IsEnabled });
        return (200, JsonSerializer.Serialize(new { id, is_enabled = !profile.IsEnabled }));
    }

    // POST /v1/dictation/start
    private async Task<(int, string)> HandleDictationStart()
    {
        if (_dictation.IsRecording)
            return (409, JsonSerializer.Serialize(new { error = "Already recording" }));

        await Application.Current.Dispatcher.InvokeAsync(
            () => _dictation.StartRecordingAsync());
        return (200, JsonSerializer.Serialize(new { started = true }));
    }

    // POST /v1/dictation/stop
    private async Task<(int, string)> HandleDictationStop()
    {
        if (!_dictation.IsRecording)
            return (409, JsonSerializer.Serialize(new { error = "Not recording" }));

        await Application.Current.Dispatcher.InvokeAsync(
            () => _dictation.StopRecordingAsync());
        return (200, JsonSerializer.Serialize(new { stopped = true }));
    }

    // GET /v1/dictation/status
    private (int, string) HandleDictationStatus()
    {
        var result = new
        {
            state = _dictation.State.ToString().ToLowerInvariant(),
            is_recording = _dictation.IsRecording,
            active_model = _modelManager.ActiveModelId,
            active_profile = _dictation.ActiveProfileName
        };
        return (200, JsonSerializer.Serialize(result));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _cts?.Dispose();
            _disposed = true;
        }
    }
}
