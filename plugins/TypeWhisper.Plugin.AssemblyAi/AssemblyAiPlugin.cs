using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

/// <summary>AssemblyAI transcription for the portable host, independent of the legacy WPF provider.</summary>
public sealed partial class AssemblyAiPlugin : ITranscriptionEnginePlugin, IApiKeyPlugin,
    IPluginProfileSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    private const string BaseUrl = "https://api.assemblyai.com";
    private const string SyncUrl = "https://sync.assemblyai.com/v1/transcribe";
    private readonly HttpClient _http;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _jobTimeout;
    private IPluginHostServices? _host;
    // Recordings whose Sync request failed. The host retries a transient failure of the async fallback with the same
    // recording, which then goes to the async API at once instead of a second, possibly billed, Sync request.
    private readonly Queue<string> _failedSyncRecordings = new();
    private const int FailedSyncRecordingLimit = 16;
    private string? _apiKey;
    private Configuration _configuration = new(AssemblyAiModels.DefaultId, false, "api-key");
    // Profiles saved before 1.2.0 have no mode and get the Live default.
    private sealed record Configuration(string Model, bool SpeakerDiarizationEnabled, string? SecretName, string? Mode = AssemblyAiModes.Live);

    /// <summary>Creates the provider with its own HTTP transport.</summary>
    public AssemblyAiPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) }) { }
    internal AssemblyAiPlugin(HttpClient http, TimeSpan? pollInterval = null, TimeSpan? jobTimeout = null)
    {
        _http = http;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _jobTimeout = jobTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.assemblyai";
    /// <inheritdoc />
    public string PluginName => "AssemblyAI";
    /// <inheritdoc />
    public string PluginVersion => "1.2.0";
    /// <inheritdoc />
    public string ProviderId => "assemblyai";
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public bool IsConfigured => _host is not null && !string.IsNullOrWhiteSpace(_apiKey);
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = AssemblyAiModels.All.Select(m =>
        new PluginModelInfo(m.Id, m.Name) { LanguageCodes = m.Languages, LanguageCount = m.Languages.Count, IsRecommended = m.IsPro }).ToArray();
    /// <inheritdoc />
    public string? SelectedModelId => _configuration.Model;
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    /// <remarks>Only the Live mode streams; Sync and Async show no live preview from AssemblyAI, so their price is what they cost.</remarks>
    public bool SupportsStreaming => Mode == AssemblyAiModes.Live && !_configuration.SpeakerDiarizationEnabled;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsStructuredDictionaryTerms => true;
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => true;
    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => Model.Budget;
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => Model.Languages;
    private AssemblyAiModel Model => AssemblyAiModels.Resolve(_configuration.Model);
    internal string Mode => AssemblyAiModes.Normalize(_configuration.Mode);

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<Configuration>("configuration")
            ?? new(host.GetSetting<string>("selectedModel") ?? AssemblyAiModels.DefaultId,
                host.GetSetting<bool?>("speakerDiarizationEnabled") ?? false, "api-key");
        var normalized = saved with { Model = AssemblyAiModels.Resolve(saved.Model).Id, Mode = AssemblyAiModes.Normalize(saved.Mode) };
        var key = normalized.SecretName is null ? null : await host.LoadSecretAsync(normalized.SecretName);
        if (saved != normalized) host.SetSetting("configuration", normalized);
        _configuration = normalized;
        _apiKey = NormalizeKey(key);
        _host = host;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() { _host = null; _apiKey = null; return Task.CompletedTask; }
    /// <inheritdoc />
    public void SelectModel(string modelId) => Commit(_configuration with { Model = AssemblyAiModels.Require(modelId).Id });

    /// <inheritdoc />
    public bool SupportsStreamingForPrompt(string? prompt)
    {
        var terms = AssemblyAiModels.Terms(prompt, Model);
        // Preserve the complete REST dictionary if it exceeds the streaming protocol budget.
        return SupportsStreaming && terms.Count <= 100 && terms.All(t => t.Length <= 50);
    }

    /// <inheritdoc />
    /// <remarks>A dictionary beyond the streaming budget still gets live text; the complete recording keeps every term.</remarks>
    public bool SupportsStreamingPreviewForPrompt(string? prompt) => SupportsStreaming && !SupportsStreamingForPrompt(prompt);

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) => StartStreamAsync(language, null, ct);
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct) =>
        StartStreamAsync(languageHints.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)), prompt, ct);

    private async Task<IStreamingSession> StartStreamAsync(string? language, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = RequireKey();
        if (!SupportsStreamingForPrompt(prompt)) throw new NotSupportedException("This configuration requires recorded-audio transcription.");
        return await AssemblyAiStreamingSession.ConnectAsync(key, Model, language, prompt, ct);
    }

    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = RequireKey();
        if (translate) throw new NotSupportedException("AssemblyAI does not support native translation.");
        if (wavAudio.Length == 0) throw new ArgumentException("Audio is empty.", nameof(wavAudio));
        var model = Model;
        var diarization = _configuration.SpeakerDiarizationEnabled;
        var normalizedLanguage = AssemblyAiModels.Language(language);
        if (normalizedLanguage is not null && !model.Languages.Contains(normalizedLanguage))
            throw new NotSupportedException("The selected AssemblyAI model does not support this language.");

        var duration = WavDuration(wavAudio);
        if (SyncRequestFor(model, Mode, diarization, normalizedLanguage, prompt, duration) is { } sync)
        {
            var recording = Convert.ToHexString(SHA256.HashData(wavAudio));
            if (!SyncFailedBefore(recording))
            {
                try { return await TranscribeSyncAsync(wavAudio, sync, key, duration, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException
                    && ex is not PluginRequestException { FailureKind: PluginRequestFailureKind.Authentication })
                {
                    // A failed Sync request is not sent again: AssemblyAI may have received and billed it.
                    RememberFailedSync(recording);
                    var reason = ex is PluginRequestException request ? request.FailureKind.ToString() : "Timeout";
                    _host?.Log(PluginLogLevel.Warning, $"AssemblyAI Sync API failed ({reason}); transcribing with the async API.");
                }
            }
        }
        var started = Stopwatch.StartNew();

        using var uploadRequest = Request(HttpMethod.Post, "/v2/upload", key);
        uploadRequest.Content = new ByteArrayContent(wavAudio);
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var upload = await ReadAsync(uploadRequest, ct);
        var uploadUrl = RequiredString(upload.RootElement, "upload_url");
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw InvalidResponse();
        using var submitRequest = Request(HttpMethod.Post, "/v2/transcript", key);
        submitRequest.Content = new StringContent(JsonSerializer.Serialize(SubmitBody(uploadUrl, model, normalizedLanguage, prompt, diarization)), Encoding.UTF8, "application/json");
        using var submitted = await ReadAsync(submitRequest, ct);
        var id = RequiredString(submitted.RootElement, "id");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_jobTimeout);
        try
        {
            while (true)
            {
                await Task.Delay(_pollInterval, deadline.Token);
                using var pollRequest = Request(HttpMethod.Get, "/v2/transcript/" + Uri.EscapeDataString(id), key);
                using var poll = await ReadAsync(pollRequest, deadline.Token);
                switch (RequiredString(poll.RootElement, "status"))
                {
                    case "completed":
                        _host?.Log(PluginLogLevel.Info, $"AssemblyAI async API returned the transcript after {started.ElapsedMilliseconds} ms.");
                        return ParseCompleted(poll.RootElement, normalizedLanguage, diarization);
                    case "queued": case "processing": continue;
                    case "error": throw new PluginRequestException("AssemblyAI could not transcribe the recording.", PluginRequestFailureKind.InvalidRequest);
                    default: throw InvalidResponse();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("AssemblyAI transcription timed out.", PluginRequestFailureKind.Timeout); }
    }

    private bool SyncFailedBefore(string recording)
    {
        lock (_failedSyncRecordings) return _failedSyncRecordings.Contains(recording);
    }

    private void RememberFailedSync(string recording)
    {
        lock (_failedSyncRecordings)
        {
            _failedSyncRecordings.Enqueue(recording);
            while (_failedSyncRecordings.Count > FailedSyncRecordingLimit) _failedSyncRecordings.Dequeue();
        }
    }

    /// <summary>The Sync API request for a recording.</summary>
    internal sealed record SyncRequest(string? Language, IReadOnlyList<string> Keyterms);

    /// <summary>
    /// Null when the recording needs the async API: the Async mode, a model without a Sync API, speaker labels,
    /// a language or dictionary the Sync API would reject, or audio over its length limit.
    /// </summary>
    internal static SyncRequest? SyncRequestFor(AssemblyAiModel model, string mode, bool diarization, string? language, string? prompt, TimeSpan? duration)
    {
        if (mode == AssemblyAiModes.Async || diarization || model.Sync is not { } limits || duration > limits.MaxDuration) return null;
        var code = AssemblyAiModels.Language(language);
        if (code is not null && !limits.Languages.Contains(code)) return null;
        var terms = AssemblyAiModels.Terms(prompt, model);
        if (terms.Count > limits.MaxTerms || terms.Sum(t => t.Length) > limits.MaxTermCharacters) return null;
        return new(code, terms);
    }

    private async Task<PluginTranscriptionResult> TranscribeSyncAsync(byte[] wavAudio, SyncRequest sync, string key, TimeSpan? duration, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncUrl);
        request.Headers.Add("Authorization", key);
        request.Headers.Add("X-AAI-Model", AssemblyAiModels.DefaultId);
        request.Content = SyncContent(wavAudio, sync);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The server gives up after 30 s; the rest covers the upload.
        deadline.CancelAfter(TimeSpan.FromSeconds(45) + TimeSpan.FromSeconds(wavAudio.Length / 1_000_000.0));
        using var response = await ReadAsync(request, deadline.Token);
        var root = response.RootElement;
        if (!root.TryGetProperty("text", out var textField) || textField.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw InvalidResponse();
        var seconds = Number(root, "audio_duration_ms") is { } ms ? ms / 1000 : duration?.TotalSeconds ?? 0;
        _host?.Log(PluginLogLevel.Info, $"AssemblyAI Sync API returned the transcript after {started.ElapsedMilliseconds} ms.");
        // The Sync API reports no detected language.
        return new(textField.GetString() ?? "", sync.Language, seconds, NoSpeechProbability: null);
    }

    internal static MultipartFormDataContent SyncContent(byte[] wavAudio, SyncRequest sync)
    {
        var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wavAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "audio", "audio.wav");
        var config = new Dictionary<string, object>();
        if (sync.Language is { } language) config["language_codes"] = new[] { language };
        if (sync.Keyterms.Count > 0) config["keyterms_prompt"] = sync.Keyterms;
        if (config.Count > 0)
        {
            var json = new StringContent(JsonSerializer.Serialize(config), Encoding.UTF8);
            json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Add(json, "config");
        }
        return content;
    }

    /// <summary>The length of a RIFF/WAVE recording, or null when the header cannot be read.</summary>
    internal static TimeSpan? WavDuration(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return null;
        long byteRate = 0, offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.AsSpan((int)offset, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan((int)offset + 4, 4)), body = offset + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= wav.Length)
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan((int)body + 8, 4));
            else if (id.SequenceEqual("data"u8))
                // A recorder that streams its WAV may leave the data size open; the bytes present count.
                return byteRate > 0 ? TimeSpan.FromSeconds(Math.Min(size, wav.Length - body) / (double)byteRate) : null;
            offset = body + size + (size & 1);
        }
        return null;
    }

    internal static Dictionary<string, object> SubmitBody(string url, AssemblyAiModel model, string? language, string? prompt, bool diarization)
    {
        var body = new Dictionary<string, object> { ["audio_url"] = url, ["speech_models"] = new[] { model.Id } };
        if (AssemblyAiModels.Language(language) is { } code) body["language_code"] = code;
        else body["language_detection"] = true;
        if (diarization) body["speaker_labels"] = true;
        var terms = AssemblyAiModels.Terms(prompt, model);
        if (terms.Count > 0)
        {
            body[model.IsPro ? "keyterms_prompt" : "word_boost"] = terms;
            if (!model.IsPro) body["boost_param"] = "high";
        }
        return body;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Add("Authorization", key);
        return request;
    }

    private async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var kind = status switch
                {
                    401 => PluginRequestFailureKind.Authentication, 403 => PluginRequestFailureKind.Permission,
                    408 => PluginRequestFailureKind.Timeout, 429 => PluginRequestFailureKind.RateLimit,
                    413 => PluginRequestFailureKind.RequestTooLarge, >= 500 => PluginRequestFailureKind.ServerError,
                    _ => PluginRequestFailureKind.InvalidRequest
                };
                throw new PluginRequestException("AssemblyAI rejected the request.", kind, status, response.Headers.RetryAfter?.Delta);
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            ct.ThrowIfCancellationRequested();
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw InvalidResponse(); }
            return doc;
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("AssemblyAI request timed out.", PluginRequestFailureKind.Timeout); }
        catch (HttpRequestException) { throw new PluginRequestException("Could not reach AssemblyAI.", PluginRequestFailureKind.Network); }
    }

    private string RequireKey() => IsConfigured ? _apiKey! : throw new PluginRequestException("Configure an AssemblyAI API key first.", PluginRequestFailureKind.Configuration);
    internal static PluginRequestException InvalidResponse() => new("AssemblyAI returned an invalid or incomplete response.", PluginRequestFailureKind.OutputIncomplete);
    internal static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static string RequiredString(JsonElement root, string name) => OptionalString(root, name) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : throw InvalidResponse();
    /// <inheritdoc />
    public void Dispose() { _apiKey = null; _host = null; _http.Dispose(); }
}
