using System.Net.Http.Headers;
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
    private readonly HttpClient _http;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _jobTimeout;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private Configuration _configuration = new(AssemblyAiModels.DefaultId, false, "api-key");
    private sealed record Configuration(string Model, bool SpeakerDiarizationEnabled, string? SecretName);

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
    public string PluginVersion => "1.1.0";
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
    public bool SupportsStreaming => !_configuration.SpeakerDiarizationEnabled;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => true;
    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => Model.Budget;
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => Model.Languages;
    private AssemblyAiModel Model => AssemblyAiModels.Resolve(_configuration.Model);

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<Configuration>("configuration")
            ?? new(host.GetSetting<string>("selectedModel") ?? AssemblyAiModels.DefaultId,
                host.GetSetting<bool?>("speakerDiarizationEnabled") ?? false, "api-key");
        var normalized = saved with { Model = AssemblyAiModels.Resolve(saved.Model).Id };
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
                    case "completed": return ParseCompleted(poll.RootElement, normalizedLanguage, diarization);
                    case "queued": case "processing": continue;
                    case "error": throw new PluginRequestException("AssemblyAI could not transcribe the recording.", PluginRequestFailureKind.InvalidRequest);
                    default: throw InvalidResponse();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("AssemblyAI transcription timed out.", PluginRequestFailureKind.Timeout); }
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
