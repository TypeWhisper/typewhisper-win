using System.IO;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides http api service behavior.
/// </summary>
public sealed class HttpApiService : ILocalApiServer, IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IHistoryService _history;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly ITranslationService _translation;
    private readonly DictationViewModel _dictation;
    private readonly IWorkflowService _workflows;
    private readonly Func<string> _apiTokenProvider;
    private readonly Dispatcher? _dispatcher;

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

    /// <summary>
    /// Gets whether the service is currently running.
    /// </summary>
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>
    /// Initializes a new instance of the HttpApiService class.
    /// </summary>
    public HttpApiService(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IHistoryService history,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        DictationViewModel dictation,
        IWorkflowService workflows,
        Func<string>? apiTokenProvider = null)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _history = history;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _translation = translation;
        _dictation = dictation;
        _workflows = workflows;
        _apiTokenProvider = apiTokenProvider ?? LoadOrCreateApiToken;
        _dispatcher = CaptureActiveDispatcher();
    }

    /// <summary>
    /// Starts the service or session.
    /// </summary>
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
        WriteDiscoveryFiles(port, _apiTokenProvider());

        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
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
        DeleteDiscoveryFiles();
    }

    private static void WriteDiscoveryFiles(int port, string token)
    {
        try
        {
            Directory.CreateDirectory(TypeWhisperEnvironment.BasePath);
            File.WriteAllText(
                TypeWhisperEnvironment.ApiPortFilePath,
                port.ToString(CultureInfo.InvariantCulture));
            var discovery = JsonSerializer.Serialize(new
            {
                version = 1,
                port,
                token
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(TypeWhisperEnvironment.ApiDiscoveryFilePath, discovery);
        }
        catch (IOException ex) { LogDiscoveryWriteFailure(ex); }
        catch (UnauthorizedAccessException ex) { LogDiscoveryWriteFailure(ex); }
        catch (NotSupportedException ex) { LogDiscoveryWriteFailure(ex); }
        catch (System.Security.SecurityException ex) { LogDiscoveryWriteFailure(ex); }
    }

    private static void DeleteDiscoveryFiles()
    {
        TryDelete(TypeWhisperEnvironment.ApiPortFilePath);
        TryDelete(TypeWhisperEnvironment.ApiDiscoveryFilePath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex) { LogDeleteFailure(path, ex); }
        catch (UnauthorizedAccessException ex) { LogDeleteFailure(path, ex); }
        catch (NotSupportedException ex) { LogDeleteFailure(path, ex); }
        catch (System.Security.SecurityException ex) { LogDeleteFailure(path, ex); }
    }

    private static void LogDiscoveryWriteFailure(Exception ex) =>
        System.Diagnostics.Debug.WriteLine($"[HttpApi] Failed to write API discovery files: {ex.Message}");

    private static void LogDeleteFailure(string path, Exception ex) =>
        System.Diagnostics.Debug.WriteLine($"[HttpApi] Failed to delete {path}: {ex.Message}");

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
            foreach (var (name, value) in apiResponse.Headers)
                response.Headers[name] = value;

            var bytes = Encoding.UTF8.GetBytes(apiResponse.Body);
            response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
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
            return new HttpApiResponse(204, "", "text/plain");

        if (!IsKnownRoute(request.Path, request.Method))
            return Error(404, "Not found");

        if (RequiresAuthentication(request) && !HasValidApiToken(request))
            return Error(
                401,
                "Missing or invalid API token",
                new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" });

        try
        {
            return (request.Path, request.Method) switch
            {
                ("/v1/status", "GET") => HandleStatus(),
                ("/v1/models", "GET") => HandleModels(),
                ("/v1/transcribe", "POST") => await HandleTranscribe(request, ct),
                ("/v1/transcribe/local-file", "POST") => await HandleTranscribeLocalFile(request, ct),
                ("/v1/history", "GET") => HandleHistorySearch(request),
                ("/v1/history", "DELETE") => HandleHistoryDelete(request),
                ("/v1/dictation/start", "POST") => await HandleDictationStart(),
                ("/v1/dictation/stop", "POST") => await HandleDictationStop(),
                ("/v1/dictation/status", "GET") => HandleDictationStatus(),
                ("/v1/dictation/transcription", "GET") => HandleDictationTranscription(request),
                ("/v1/rules", "GET") => HandleGetRules(),
                ("/v1/profiles", "GET") => HandleGetProfiles(),
                ("/v1/rules/toggle", "PUT") => HandleToggleRule(request),
                ("/v1/profiles/toggle", "PUT") => HandleToggleRule(request),
                ("/v1/dictionary/terms", "GET") => HandleGetDictionaryTerms(),
                ("/v1/dictionary/terms", "PUT") => await HandlePutDictionaryTerms(request),
                ("/v1/dictionary/terms", "DELETE") => await HandleDeleteDictionaryTerms(request),
                ("/v1/dictionary/corrections", "GET") => HandleGetDictionaryCorrections(),
                ("/v1/dictionary/corrections", "PUT") => await HandlePutDictionaryCorrection(request),
                ("/v1/dictionary/corrections", "DELETE") => await HandleDeleteDictionaryCorrection(request),
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
        catch (DispatcherUnavailableException ex)
        {
            return Error(503, ex.Message);
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
        var accelerationStatus = activePlugin?.AccelerationStatus;

        return Json(new
        {
            status = activePlugin is not null && _modelManager.Engine.IsModelLoaded ? "ready" : "no_model",
            engine = activePlugin?.ProviderId,
            model = activeModel,
            active_model = _modelManager.ActiveModelId,
            api_version = "1.0",
            supports_streaming = activePlugin?.SupportsStreaming ?? false,
            supports_translation = activePlugin?.SupportsTranslation ?? false,
            acceleration = activePlugin is null || accelerationStatus is null
                ? null
                : new
                {
                    preference = AppSettings.NormalizeLocalModelAcceleration(_settings.Current.LocalModelAcceleration),
                    active_backend = FormatAccelerationBackend(accelerationStatus.ActiveBackend),
                    display_text = accelerationStatus.DisplayText,
                    detail = accelerationStatus.Detail,
                    requires_restart = accelerationStatus.RequiresRestart
                }
        });
    }

    private HttpApiResponse HandleModels()
    {
        var selectedModelId = _settings.Current.SelectedModelId;
        var models = _modelManager.PluginManager.TranscriptionEngines
            .SelectMany(engine => engine.TranscriptionModels.Select(model =>
            {
                var fullId = ModelManagerService.GetPluginModelId(engine.GetTranscriptionSelectionId(), model.Id);
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

        var tempPath = Path.Join(
            Path.GetTempPath(),
            $"tw_api_{Guid.NewGuid():N}.{SanitizeExtension(transcribeRequest.FileExtension)}");

        try
        {
            await File.WriteAllBytesAsync(tempPath, transcribeRequest.AudioData, ct);
            var samples = await _audioFile.LoadAudioAsync(tempPath, ct);
            return await TranscribeSamplesAsync(samples, TranscribeOptions.From(transcribeRequest), ct);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException ex) { LogTempDeleteFailure(tempPath, ex); }
            catch (UnauthorizedAccessException ex) { LogTempDeleteFailure(tempPath, ex); }
            catch (NotSupportedException ex) { LogTempDeleteFailure(tempPath, ex); }
            catch (System.Security.SecurityException ex) { LogTempDeleteFailure(tempPath, ex); }
        }
    }

    private static void LogTempDeleteFailure(string path, Exception ex) =>
        System.Diagnostics.Debug.WriteLine($"[HttpApi] Failed to delete temporary audio file {path}: {ex.Message}");

    private async Task<HttpApiResponse> HandleTranscribeLocalFile(HttpApiRequest request, CancellationToken ct)
    {
        var transcribeRequest = HttpApiRequestParser.ParseTranscribeLocalFile(request);

        if (!File.Exists(transcribeRequest.Path))
            return Error(400, "File not found");

        if (!AudioFileService.IsSupported(transcribeRequest.Path))
            return Error(400, "Unsupported audio format");

        var samples = await _audioFile.LoadAudioAsync(transcribeRequest.Path, ct);
        return await TranscribeSamplesAsync(samples, TranscribeOptions.From(transcribeRequest), ct);
    }

    private async Task<HttpApiResponse> TranscribeSamplesAsync(
        float[] samples,
        TranscribeOptions transcribeRequest,
        CancellationToken ct)
    {
        await using var modelScope = await _modelManager.BeginTranscriptionRequestAsync(
            transcribeRequest.Engine,
            transcribeRequest.Model,
            transcribeRequest.AwaitDownload,
            ct);

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
            active_workflow = _dictation.ActiveWorkflowName
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

    private HttpApiResponse HandleGetRules()
    {
        var rules = _workflows.Workflows
            .OrderBy(workflow => workflow.SortOrder)
            .Select(RuleFromWorkflow)
            .ToList();

        return Json(new { rules, count = rules.Count });
    }

    private HttpApiResponse HandleGetProfiles()
    {
        var profiles = _workflows.Workflows
            .OrderBy(workflow => workflow.SortOrder)
            .Select(RuleFromWorkflow)
            .ToList();

        return Json(new { profiles, count = profiles.Count });
    }

    private HttpApiResponse HandleToggleRule(HttpApiRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrWhiteSpace(id))
            return Error(400, "Missing or invalid 'id' query parameter");

        var workflow = _workflows.GetWorkflow(id)
            ?? _workflows.Workflows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
        if (workflow is null)
            return Error(404, "Rule not found");

        _workflows.ToggleWorkflow(id);
        var toggled = _workflows.GetWorkflow(id) ?? workflow with { IsEnabled = !workflow.IsEnabled };
        var response = RuleFromWorkflow(toggled);
        return Json(response);
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

    private async Task<HttpApiResponse> HandleDeleteDictionaryTerms(HttpApiRequest request)
    {
        if (request.Body.Length == 0)
            return Error(400, "Missing JSON body");

        DictionaryTermDeleteRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DictionaryTermDeleteRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return Error(400, "Invalid JSON body");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Term))
            return Error(400, "Missing or empty 'term'");

        var result = await InvokeOnDispatcherAsync(() =>
        {
            var deleted = _dictionary.DeleteTerm(payload.Term);
            var terms = _dictionary.GetEnabledTerms();
            return Task.FromResult((deleted, terms.Count));
        });

        return Json(new { deleted = result.deleted, count = result.Count });
    }

    private HttpApiResponse HandleGetDictionaryCorrections()
    {
        var corrections = CorrectionDtos(_dictionary.GetEnabledCorrections());
        return Json(new { corrections, count = corrections.Count });
    }

    private async Task<HttpApiResponse> HandlePutDictionaryCorrection(HttpApiRequest request)
    {
        var payload = ParseDictionaryCorrectionMutation(request);
        if (payload is null)
            return Error(400, "Invalid JSON body");

        if (string.IsNullOrWhiteSpace(payload.Original))
            return Error(400, "Missing or empty 'original'");

        if (payload.Replacement is null)
            return Error(400, "Missing 'replacement'");

        var corrections = await InvokeOnDispatcherAsync(() =>
        {
            _dictionary.UpsertCorrection(payload.Original, payload.Replacement, payload.CaseSensitive);
            return Task.FromResult(CorrectionDtos(_dictionary.GetEnabledCorrections()));
        });

        return Json(new { corrections, count = corrections.Count });
    }

    private async Task<HttpApiResponse> HandleDeleteDictionaryCorrection(HttpApiRequest request)
    {
        var payload = ParseDictionaryCorrectionMutation(request);
        if (payload is null)
            return Error(400, "Invalid JSON body");

        if (string.IsNullOrWhiteSpace(payload.Original))
            return Error(400, "Missing or empty 'original'");

        var result = await InvokeOnDispatcherAsync(() =>
        {
            var deleted = _dictionary.DeleteCorrection(payload.Original);
            var corrections = CorrectionDtos(_dictionary.GetEnabledCorrections());
            return Task.FromResult((deleted, corrections));
        });

        return Json(new { deleted = result.deleted, corrections = result.corrections, count = result.corrections.Count });
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
    }

    private Func<string, string>? GetVocabularyBooster() =>
        _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null;

    private static bool IsKnownRoute(string path, string method) =>
        (path, method) switch
        {
            ("/v1/status", "GET") => true,
            ("/v1/models", "GET") => true,
            ("/v1/transcribe", "POST") => true,
            ("/v1/transcribe/local-file", "POST") => true,
            ("/v1/history", "GET") => true,
            ("/v1/history", "DELETE") => true,
            ("/v1/dictation/start", "POST") => true,
            ("/v1/dictation/stop", "POST") => true,
            ("/v1/dictation/status", "GET") => true,
            ("/v1/dictation/transcription", "GET") => true,
            ("/v1/rules", "GET") => true,
            ("/v1/profiles", "GET") => true,
            ("/v1/rules/toggle", "PUT") => true,
            ("/v1/profiles/toggle", "PUT") => true,
            ("/v1/dictionary/terms", "GET") => true,
            ("/v1/dictionary/terms", "PUT") => true,
            ("/v1/dictionary/terms", "DELETE") => true,
            ("/v1/dictionary/corrections", "GET") => true,
            ("/v1/dictionary/corrections", "PUT") => true,
            ("/v1/dictionary/corrections", "DELETE") => true,
            _ => false
        };

    private bool RequiresAuthentication(HttpApiRequest request) =>
        _settings.Current.ApiServerRequiresAuthentication &&
        !string.Equals(request.Path, "/v1/status", StringComparison.Ordinal);

    private bool HasValidApiToken(HttpApiRequest request)
    {
        var expected = _apiTokenProvider();
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        return TokenMatches(ExtractBearerToken(request.Headers), expected)
            || TokenMatches(Header(request.Headers, "x-typewhisper-api-token"), expected);
    }

    private static string? ExtractBearerToken(IReadOnlyDictionary<string, string> headers)
    {
        var authorization = Header(headers, "authorization");
        if (string.IsNullOrWhiteSpace(authorization))
            return null;

        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = authorization[bearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static bool TokenMatches(string? candidate, string expected)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return candidateBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static IReadOnlyList<DictionaryCorrectionDto> CorrectionDtos(IReadOnlyList<DictionaryEntry> entries) =>
        entries
            .Select(entry => new DictionaryCorrectionDto(
                entry.Original,
                entry.Replacement ?? "",
                entry.CaseSensitive))
            .ToList();

    private DictionaryCorrectionMutationRequest? ParseDictionaryCorrectionMutation(HttpApiRequest request)
    {
        if (request.Body.Length == 0)
            return null;

        try
        {
            return JsonSerializer.Deserialize<DictionaryCorrectionMutationRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private RuleDto RuleFromWorkflow(Workflow workflow)
    {
        var language = ResolveWorkflowLanguage(workflow);
        var translationTarget = FirstNonBlank(
            workflow.Behavior.TranslationTarget,
            WorkflowSetting(workflow, "targetLanguage"),
            WorkflowSetting(workflow, "target"));

        return new RuleDto(
            workflow.Id,
            workflow.Name,
            workflow.Name,
            workflow.Name,
            workflow.IsEnabled,
            workflow.SortOrder,
            workflow.Trigger.ProcessNames.ToList(),
            workflow.Trigger.WebsitePatterns.ToList(),
            language.InputLanguage,
            language.Mode,
            language.Hints,
            translationTarget);
    }

    private static WorkflowLanguageDto ResolveWorkflowLanguage(Workflow workflow)
    {
        var configured = FirstNonBlank(
            workflow.Behavior.InputLanguage,
            WorkflowSetting(workflow, "inputLanguage"),
            WorkflowSetting(workflow, "language"));

        if (string.IsNullOrWhiteSpace(configured))
            return new WorkflowLanguageDto("inherit_global", null, []);

        var trimmed = configured.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var hints = JsonSerializer.Deserialize<IReadOnlyList<string>>(trimmed) ?? [];
                var normalized = hints
                    .Select(value => value.Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();

                if (normalized.Count > 0)
                    return new WorkflowLanguageDto("multiple", null, normalized);
            }
            catch (JsonException)
            {
                // Fall through and expose the raw setting as an exact language.
            }
        }

        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return new WorkflowLanguageDto("auto", "auto", []);

        if (trimmed.Equals("global", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("inherit_global", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowLanguageDto("inherit_global", null, []);
        }

        return new WorkflowLanguageDto("exact", trimmed, []);
    }

    private static string? WorkflowSetting(Workflow workflow, string key) =>
        workflow.Behavior.Settings.TryGetValue(key, out var value) ? value : null;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string LoadOrCreateApiToken()
    {
        try
        {
            if (File.Exists(TypeWhisperEnvironment.ApiTokenFilePath))
            {
                var encrypted = File.ReadAllText(TypeWhisperEnvironment.ApiTokenFilePath).Trim();
                var token = ApiKeyProtection.Decrypt(encrypted);
                if (!string.IsNullOrWhiteSpace(token))
                    return token;
            }
        }
        catch (IOException ex) { LogTokenReadFailure(ex); }
        catch (UnauthorizedAccessException ex) { LogTokenReadFailure(ex); }
        catch (NotSupportedException ex) { LogTokenReadFailure(ex); }
        catch (CryptographicException ex) { LogTokenReadFailure(ex); }
        catch (System.Security.SecurityException ex) { LogTokenReadFailure(ex); }

        var generated = GenerateApiToken();
        try
        {
            Directory.CreateDirectory(TypeWhisperEnvironment.BasePath);
            File.WriteAllText(TypeWhisperEnvironment.ApiTokenFilePath, ApiKeyProtection.Encrypt(generated));
        }
        catch (IOException ex) { LogTokenPersistFailure(ex); }
        catch (UnauthorizedAccessException ex) { LogTokenPersistFailure(ex); }
        catch (NotSupportedException ex) { LogTokenPersistFailure(ex); }
        catch (CryptographicException ex) { LogTokenPersistFailure(ex); }
        catch (System.Security.SecurityException ex) { LogTokenPersistFailure(ex); }

        return generated;
    }

    private static void LogTokenReadFailure(Exception ex) =>
        System.Diagnostics.Debug.WriteLine($"[HttpApi] Failed to read API token: {ex.Message}");

    private static void LogTokenPersistFailure(Exception ex) =>
        System.Diagnostics.Debug.WriteLine($"[HttpApi] Failed to persist API token: {ex.Message}");

    private static string GenerateApiToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static HttpApiResponse Json<T>(T value, int statusCode = 200) =>
        new(statusCode, JsonSerializer.Serialize(value, JsonOptions));

    private static HttpApiResponse Error(
        int statusCode,
        string message,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(
            statusCode,
            JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = ErrorCode(statusCode),
                    message
                }
            }, JsonOptions),
            "application/json",
            headers);

    private sealed record TranscribeOptions(
        string? Language,
        IReadOnlyList<string> LanguageHints,
        TranscriptionTask Task,
        string? TargetLanguage,
        string ResponseFormat,
        string? Prompt,
        string? Engine,
        string? Model,
        bool AwaitDownload)
    {
        /// <summary>
        /// Performs from.
        /// </summary>
        public static TranscribeOptions From(TranscribeApiRequest request) =>
            new(
                request.Language,
                request.LanguageHints,
                request.Task,
                request.TargetLanguage,
                request.ResponseFormat,
                request.Prompt,
                request.Engine,
                request.Model,
                request.AwaitDownload);

        /// <summary>
        /// Performs from.
        /// </summary>
        public static TranscribeOptions From(LocalFileTranscribeApiRequest request) =>
            new(
                request.Language,
                request.LanguageHints,
                request.Task,
                request.TargetLanguage,
                request.ResponseFormat,
                request.Prompt,
                request.Engine,
                request.Model,
                request.AwaitDownload);
    }

    private sealed record RuleDto(
        string Id,
        string Name,
        string RuleName,
        string ProfileName,
        bool IsEnabled,
        int Priority,
        IReadOnlyList<string> BundleIdentifiers,
        IReadOnlyList<string> UrlPatterns,
        string? InputLanguage,
        string LanguageMode,
        IReadOnlyList<string> LanguageHints,
        string? TranslationTargetLanguage);

    private sealed record WorkflowLanguageDto(
        string Mode,
        string? InputLanguage,
        IReadOnlyList<string> Hints);

    private sealed record DictionaryCorrectionDto(
        [property: JsonPropertyName("original")] string Original,
        [property: JsonPropertyName("replacement")] string Replacement,
        [property: JsonPropertyName("caseSensitive")] bool CaseSensitive);

    private sealed record DictionaryCorrectionMutationRequest
    {
        /// <summary>
        /// Gets or sets the original value.
        /// </summary>
        public string? Original { get; init; }
        /// <summary>
        /// Gets or sets the replacement value.
        /// </summary>
        public string? Replacement { get; init; }

        /// <summary>
        /// Gets or sets the case sensitive value.
        /// </summary>
        [JsonPropertyName("caseSensitive")]
        public bool CaseSensitive { get; init; }
    }

    private sealed record DictionaryTermDeleteRequest
    {
        /// <summary>
        /// Gets or sets the term value.
        /// </summary>
        public string? Term { get; init; }
    }

    private static string ErrorCode(int statusCode) => statusCode switch
    {
        400 => "bad_request",
        401 => "unauthorized",
        404 => "not_found",
        409 => "conflict",
        413 => "payload_too_large",
        501 => "not_implemented",
        503 => "service_unavailable",
        _ => "error"
    };

    private static string FormatAccelerationBackend(TranscriptionAccelerationBackend backend) =>
        backend switch
        {
            TranscriptionAccelerationBackend.NvidiaCuda => AppSettings.LocalModelAccelerationNvidiaCuda,
            TranscriptionAccelerationBackend.AmdVulkan => AppSettings.LocalModelAccelerationAmdVulkan,
            TranscriptionAccelerationBackend.AmdRocm => AppSettings.LocalModelAccelerationAmdRocm,
            _ => AppSettings.LocalModelAccelerationCpu
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

    private static Dispatcher? CaptureActiveDispatcher()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished
            ? null
            : dispatcher;
    }

    private async Task<T> InvokeOnDispatcherAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
            return await action();

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            throw new DispatcherUnavailableException("Application is shutting down.");

        if (dispatcher.CheckAccess())
            return await action();

        try
        {
            var operation = dispatcher.InvokeAsync(action);
            return await await operation.Task;
        }
        catch (TaskCanceledException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new DispatcherUnavailableException("Application is shutting down.");
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new DispatcherUnavailableException("Application is shutting down.");
        }
    }

    private sealed record DictionaryTermsRequest
    {
        /// <summary>
        /// Gets or sets the terms value.
        /// </summary>
        public IReadOnlyList<string> Terms { get; init; } = [];
        /// <summary>
        /// Gets or sets the replace value.
        /// </summary>
        public bool? Replace { get; init; }
    }

    private sealed class DispatcherUnavailableException(string message) : Exception(message);
}
