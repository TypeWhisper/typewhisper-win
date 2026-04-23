using System.IO;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Services;

public sealed class HttpApiService : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IHistoryService _history;
    private readonly IProfileService _profiles;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly ITranslationService _translation;
    private readonly DictationViewModel _dictation;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int? _runningPort;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public bool IsRunning => _listener?.IsListening == true;

    public HttpApiService(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IHistoryService history,
        IProfileService profiles,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        DictationViewModel dictation)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _history = history;
        _profiles = profiles;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _translation = translation;
        _dictation = dictation;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public void Start(int port)
    {
        if (_listener is { IsListening: true } && _runningPort == port) return;
        if (_listener is { IsListening: true }) Stop();

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _runningPort = port;
        WriteApiPortFile(port);

        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_disposed) return;

        if (settings.ApiServerEnabled)
            Start(settings.ApiServerPort);
        else
            Stop();
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _listenTask = null;
        _runningPort = null;
        cts?.Dispose();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        try
        {
            ApplySettings(settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HttpApi] Failed to apply settings: {ex.Message}");
        }
    }

    private static void WriteApiPortFile(int port)
    {
        try
        {
            Directory.CreateDirectory(TypeWhisperEnvironment.BasePath);
            File.WriteAllText(
                TypeWhisperEnvironment.ApiPortFilePath,
                port.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HttpApi] Failed to write API port file: {ex.Message}");
        }
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
        var response = context.Response;

        try
        {
            var request = await HttpApiRequest.FromListenerRequestAsync(context.Request, ct);
            var apiResponse = await HandleRequestAsync(request, ct);

            response.StatusCode = apiResponse.StatusCode;
            response.ContentType = apiResponse.ContentType;
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Language, X-Language-Hints, X-Task, X-Target-Language, X-Response-Format, X-Prompt, X-Engine, X-Model";

            var bytes = Encoding.UTF8.GetBytes(apiResponse.Body);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            var apiResponse = Error(500, ex.Message);
            response.StatusCode = apiResponse.StatusCode;
            response.ContentType = apiResponse.ContentType;
            var errorBytes = Encoding.UTF8.GetBytes(apiResponse.Body);
            response.ContentLength64 = errorBytes.Length;
            await response.OutputStream.WriteAsync(errorBytes, ct);
        }
        finally
        {
            response.Close();
        }
    }

    internal async Task<HttpApiResponse> HandleRequestAsync(HttpApiRequest request, CancellationToken ct)
    {
        if (request.Method == "OPTIONS")
            return Json(new { ok = true });

        try
        {
            return (request.Path, request.Method) switch
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
                ("/v1/dictation/transcription", "GET") => HandleDictationTranscription(request),
                ("/v1/dictionary/terms", "GET") => HandleGetDictionaryTerms(),
                ("/v1/dictionary/terms", "PUT") => await HandlePutDictionaryTerms(request),
                ("/v1/dictionary/terms", "DELETE") => await HandleDeleteDictionaryTerms(),
                _ => Error(404, "Not found")
            };
        }
        catch (HttpApiRequestException ex)
        {
            return Error(ex.StatusCode, ex.Message);
        }
        catch (ModelManagerRequestException ex)
        {
            return Error(ex.StatusCode, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(500, ex.Message);
        }
    }

    private HttpApiResponse HandleStatus()
    {
        var activePlugin = _modelManager.ActiveTranscriptionPlugin;
        var activeModel = _modelManager.ActiveModelId is { } activeModelId && ModelManagerService.IsPluginModel(activeModelId)
            ? ModelManagerService.ParsePluginModelId(activeModelId).ModelId
            : activePlugin?.SelectedModelId;

        return Json(new
        {
            status = activePlugin is not null && _modelManager.Engine.IsModelLoaded ? "ready" : "no_model",
            engine = activePlugin?.ProviderId,
            model = activeModel,
            active_model = _modelManager.ActiveModelId,
            api_version = "1.0",
            supports_streaming = activePlugin?.SupportsStreaming ?? false,
            supports_translation = activePlugin?.SupportsTranslation ?? false
        });
    }

    private HttpApiResponse HandleModels()
    {
        var selectedModelId = _settings.Current.SelectedModelId;
        var models = _modelManager.PluginManager.TranscriptionEngines
            .SelectMany(engine => engine.TranscriptionModels.Select(model =>
            {
                var fullId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                var downloaded = _modelManager.IsDownloaded(fullId);
                var status = engine.SupportsModelDownload
                    ? downloaded ? "ready" : "not_downloaded"
                    : engine.IsConfigured ? "ready" : "not_configured";

                return new
                {
                    id = model.Id,
                    full_id = fullId,
                    engine = engine.ProviderId,
                    name = model.DisplayName,
                    size_description = model.SizeDescription ?? (engine.SupportsModelDownload ? "Local" : "Cloud"),
                    language_count = model.LanguageCount,
                    status,
                    selected = selectedModelId == fullId,
                    active = _modelManager.ActiveModelId == fullId,
                    downloaded,
                    loaded = _modelManager.ActiveModelId == fullId
                };
            }))
            .ToList();

        return Json(new { models });
    }

    private async Task<HttpApiResponse> HandleTranscribe(HttpApiRequest request, CancellationToken ct)
    {
        var transcribeRequest = HttpApiRequestParser.ParseTranscribe(request);

        await using var modelScope = await _modelManager.BeginTranscriptionRequestAsync(
            transcribeRequest.Engine,
            transcribeRequest.Model,
            transcribeRequest.AwaitDownload,
            ct);

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"tw_api_{Guid.NewGuid():N}.{SanitizeExtension(transcribeRequest.FileExtension)}");

        try
        {
            await File.WriteAllBytesAsync(tempPath, transcribeRequest.AudioData, ct);
            var samples = await _audioFile.LoadAudioAsync(tempPath, ct);

            var prompt = MergePrompt(
                transcribeRequest.Prompt,
                BuildLanguageHintsPrompt(transcribeRequest.LanguageHints),
                _dictionary.GetTermsForPrompt());

            var activeResult = await _modelManager.TranscribeActiveAsync(
                samples,
                transcribeRequest.Language,
                transcribeRequest.Task,
                prompt,
                ct);

            var result = activeResult.Result;
            var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
            {
                VocabularyBooster = GetVocabularyBooster(),
                DictionaryCorrector = _dictionary.ApplyCorrections
            }, ct);

            var finalText = pipelineResult.Text;
            if (!string.IsNullOrWhiteSpace(transcribeRequest.TargetLanguage))
            {
                var sourceLanguage = result.DetectedLanguage
                    ?? transcribeRequest.Language
                    ?? "en";

                try
                {
                    finalText = await _translation.TranslateAsync(
                        finalText,
                        sourceLanguage,
                        transcribeRequest.TargetLanguage,
                        ct);
                }
                catch (NotSupportedException ex)
                {
                    return Error(501, ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return Error(501, ex.Message);
                }
            }

            if (transcribeRequest.ResponseFormat.Equals("verbose_json", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new
                {
                    text = finalText,
                    language = result.DetectedLanguage,
                    duration = result.Duration,
                    processing_time = result.ProcessingTime,
                    engine = activeResult.EngineId,
                    model = activeResult.ModelId,
                    segments = result.Segments.Select(seg => new
                    {
                        text = seg.Text,
                        start = seg.Start,
                        end = seg.End
                    })
                });
            }

            return Json(new
            {
                text = finalText,
                language = result.DetectedLanguage,
                duration = result.Duration,
                processing_time = result.ProcessingTime,
                engine = activeResult.EngineId,
                model = activeResult.ModelId
            });
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private HttpApiResponse HandleHistorySearch(HttpApiRequest request)
    {
        var query = request.QueryString["q"] ?? "";
        var limit = Math.Min(ParseInt(request.QueryString["limit"], 50), 200);
        var offset = Math.Max(ParseInt(request.QueryString["offset"], 0), 0);

        var records = string.IsNullOrWhiteSpace(query)
            ? _history.Records
            : _history.Search(query);

        var paged = records.Skip(offset).Take(limit).Select(r => new
        {
            id = r.Id,
            timestamp = r.Timestamp,
            text = r.FinalText,
            raw_text = r.RawText,
            app = r.AppProcessName,
            app_name = r.AppName ?? r.AppProcessName,
            app_process_name = r.AppProcessName,
            app_bundle_id = (string?)null,
            app_url = r.AppUrl,
            duration = r.DurationSeconds,
            language = r.Language,
            engine = r.EngineUsed,
            model = r.ModelUsed,
            profile = r.ProfileName,
            words = r.WordCount,
            words_count = r.WordCount
        }).ToList();

        return Json(new
        {
            total = records.Count,
            offset,
            limit,
            records = paged,
            entries = paged
        });
    }

    private HttpApiResponse HandleHistoryDelete(HttpApiRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrEmpty(id))
            return Error(400, "Missing id parameter");

        _history.DeleteRecord(id);
        return Json(new { deleted = true, id });
    }

    private HttpApiResponse HandleProfilesList()
    {
        var profiles = _profiles.Profiles.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            is_enabled = p.IsEnabled,
            priority = p.Priority,
            process_names = p.ProcessNames,
            bundle_identifiers = p.ProcessNames,
            url_patterns = p.UrlPatterns,
            input_language = p.InputLanguage,
            translation_target = p.TranslationTarget,
            translation_target_language = p.TranslationTarget,
            selected_task = p.SelectedTask,
            model_override = p.TranscriptionModelOverride,
            prompt_action_id = p.PromptActionId
        });

        return Json(new { profiles });
    }

    private HttpApiResponse HandleProfileToggle(HttpApiRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrEmpty(id))
            return Error(400, "Missing id parameter");

        var profile = _profiles.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
            return Error(404, "Profile not found");

        _profiles.UpdateProfile(profile with { IsEnabled = !profile.IsEnabled });
        return Json(new { id, is_enabled = !profile.IsEnabled });
    }

    private async Task<HttpApiResponse> HandleDictationStart()
    {
        if (_dictation.IsRecording)
            return Error(409, "Already recording");

        var id = await InvokeOnDispatcherAsync(() => _dictation.StartRecordingForApiAsync());
        var session = _dictation.GetApiDictationSession(id);
        if (session?.Status == ApiDictationSessionStatus.Failed)
            return Error(409, session.Error ?? "Failed to start dictation");

        return Json(new { id, status = "recording" });
    }

    private async Task<HttpApiResponse> HandleDictationStop()
    {
        if (!_dictation.IsRecording)
            return Error(409, "Not recording");

        var id = await InvokeOnDispatcherAsync(() => _dictation.StopRecordingForApiAsync());
        if (id is null)
            return Error(500, "Missing active dictation session");

        return Json(new { id, status = "stopped" });
    }

    private HttpApiResponse HandleDictationStatus()
    {
        return Json(new
        {
            state = _dictation.State.ToString().ToLowerInvariant(),
            is_recording = _dictation.IsRecording,
            active_model = _modelManager.ActiveModelId,
            active_profile = _dictation.ActiveProfileName
        });
    }

    private HttpApiResponse HandleDictationTranscription(HttpApiRequest request)
    {
        var idString = request.QueryString["id"];
        if (!Guid.TryParse(idString, out var id))
            return Error(400, "Missing or invalid 'id' query parameter");

        var session = _dictation.GetApiDictationSession(id);
        if (session is null)
            return Error(404, "Dictation session not found");

        var transcription = session.Transcription is null
            ? null
            : new
            {
                text = session.Transcription.Text,
                raw_text = session.Transcription.RawText,
                timestamp = session.Transcription.Timestamp,
                app_name = session.Transcription.AppName,
                app_process_name = session.Transcription.AppProcessName,
                app_bundle_id = (string?)null,
                app_url = session.Transcription.AppUrl,
                duration = session.Transcription.Duration,
                language = session.Transcription.Language,
                engine = session.Transcription.Engine,
                model = session.Transcription.Model,
                words_count = session.Transcription.WordsCount
            };

        return Json(new
        {
            id = session.Id,
            status = session.Status.ToString().ToLowerInvariant(),
            transcription,
            error = session.Error
        });
    }

    private HttpApiResponse HandleGetDictionaryTerms()
    {
        var terms = _dictionary.GetEnabledTerms();
        return Json(new { terms, count = terms.Count });
    }

    private async Task<HttpApiResponse> HandlePutDictionaryTerms(HttpApiRequest request)
    {
        if (request.Body.Length == 0)
            return Error(400, "Missing JSON body");

        DictionaryTermsRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DictionaryTermsRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return Error(400, "Invalid JSON body");
        }

        if (payload is null)
            return Error(400, "Invalid JSON body");

        var terms = await InvokeOnDispatcherAsync(() =>
        {
            _dictionary.SetTerms(payload.Terms, payload.Replace ?? false);
            return Task.FromResult(_dictionary.GetEnabledTerms());
        });

        return Json(new { terms, count = terms.Count });
    }

    private async Task<HttpApiResponse> HandleDeleteDictionaryTerms()
    {
        await InvokeOnDispatcherAsync(() =>
        {
            _dictionary.RemoveAllTerms();
            return Task.FromResult(true);
        });

        return Json(new { deleted = true, count = 0 });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _settings.SettingsChanged -= OnSettingsChanged;
            Stop();
            _disposed = true;
        }
    }

    private Func<string, string>? GetVocabularyBooster() =>
        _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null;

    private static HttpApiResponse Json<T>(T value, int statusCode = 200) =>
        new(statusCode, JsonSerializer.Serialize(value, JsonOptions));

    private static HttpApiResponse Error(int statusCode, string message) =>
        Json(new
        {
            error = new
            {
                code = ErrorCode(statusCode),
                message
            }
        }, statusCode);

    private static string ErrorCode(int statusCode) => statusCode switch
    {
        400 => "bad_request",
        404 => "not_found",
        409 => "conflict",
        413 => "payload_too_large",
        501 => "not_implemented",
        503 => "service_unavailable",
        _ => "error"
    };

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static string SanitizeExtension(string extension)
    {
        var sanitized = new string(extension.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "tmp" : sanitized.ToLowerInvariant();
    }

    private static string? BuildLanguageHintsPrompt(IReadOnlyList<string> languageHints) =>
        languageHints.Count == 0 ? null : "Language hints: " + string.Join(", ", languageHints);

    private static string? MergePrompt(params string?[] prompts)
    {
        var parts = prompts
            .Select(p => p?.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static async Task<T> InvokeOnDispatcherAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return await action();

        var operation = dispatcher.InvokeAsync(action);
        return await await operation.Task;
    }

    private sealed record DictionaryTermsRequest
    {
        public IReadOnlyList<string> Terms { get; init; } = [];
        public bool? Replace { get; init; }
    }
}
