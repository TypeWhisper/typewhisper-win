using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using NAudio.Wave;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Groq;

/// <summary>
/// Provides groq plugin behavior.
/// </summary>
public sealed class GroqPlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin
{
    private const string BaseUrl = "https://api.groq.com/openai";
    private const int TranscriptionUploadBitRate = 48_000;
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTranscriptionHttpTimeout = TimeSpan.FromMinutes(10);
    private readonly HttpClient _httpClient;
    private readonly HttpClient _transcriptionHttpClient;
    private readonly Func<byte[], GroqTranscriptionUpload> _compressedUploadFactory;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _selectedModelId;
    private string? _selectedApiModelName;
    private string? _selectedLlmModelId;
    private List<FetchedLlmModel> _fetchedLlmModels = [];

    private static readonly IReadOnlyList<TranscriptionModelEntry> TranscriptionModelEntries =
    [
        new("whisper-large-v3", "Whisper Large V3", "whisper-large-v3", SupportsTranslation: true),
        new("whisper-large-v3-turbo", "Whisper Large V3 Turbo", "whisper-large-v3-turbo", SupportsTranslation: false),
    ];

    private static readonly IReadOnlyList<PluginModelInfo> FallbackLlmModels =
    [
        new("llama-3.3-70b-versatile", "Llama 3.3 70B"),
        new("llama-3.1-8b-instant", "Llama 3.1 8B"),
        new("openai/gpt-oss-120b", "GPT-OSS 120B"),
        new("openai/gpt-oss-20b", "GPT-OSS 20B"),
        new("moonshotai/kimi-k2-instruct-0905", "Kimi K2"),
    ];

    /// <summary>
    /// Initializes a new instance of the GroqPlugin class.
    /// </summary>
    public GroqPlugin()
        : this(CreateHttpClient(), CreateTranscriptionHttpClient())
    {
    }

    internal GroqPlugin(HttpClient httpClient)
        : this(httpClient, httpClient)
    {
    }

    internal GroqPlugin(
        HttpClient httpClient,
        HttpClient transcriptionHttpClient,
        Func<byte[], GroqTranscriptionUpload>? compressedUploadFactory = null)
    {
        _httpClient = httpClient;
        _transcriptionHttpClient = transcriptionHttpClient;
        _compressedUploadFactory = compressedUploadFactory ?? CreateCompressedUpload;
    }

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.groq";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Groq";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.3";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? TranscriptionModelEntries[0].Id;
        _selectedLlmModelId = host.GetSetting<string>("selectedLlmModel");
        _fetchedLlmModels = NormalizeFetchedLlmModels(host.GetSetting<List<FetchedLlmModel>>("fetchedLlmModels") ?? []);

        var selectedTranscription = TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId)
            ?? TranscriptionModelEntries[0];
        _selectedModelId = selectedTranscription.Id;
        _selectedApiModelName = selectedTranscription.ApiModelName;

        NormalizeSelectedLlmModel();
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new GroqSettingsView(this);

    // ITranscriptionEnginePlugin

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "groq";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Groq";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        TranscriptionModelEntries.Select(m => new PluginModelInfo(m.Id, m.DisplayName)).ToList();

    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;

    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation
    {
        get
        {
            if (!IsConfigured || _selectedModelId is null)
                return false;
            var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == _selectedModelId);
            return entry?.SupportsTranslation ?? false;
        }
    }

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        var entry = TranscriptionModelEntries.FirstOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
        _selectedApiModelName = entry.ApiModelName;
        _host?.SetSetting("selectedModel", modelId);
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured || _selectedApiModelName is null)
            throw new InvalidOperationException("Plugin not configured. API key and model required.");

        GroqTranscriptionUpload upload;
        try
        {
            ct.ThrowIfCancellationRequested();
            upload = _compressedUploadFactory(wavAudio);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw CreateCompressionException(ex);
        }
        catch (IOException)
        {
            upload = CreateWavUpload(wavAudio);
        }
        catch (InvalidOperationException)
        {
            upload = CreateWavUpload(wavAudio);
        }
        catch (COMException)
        {
            upload = CreateWavUpload(wavAudio);
        }

        return await TranscribeUploadAsync(
            _transcriptionHttpClient,
            BaseUrl,
            _apiKey!,
            _selectedApiModelName,
            upload,
            language,
            translate,
            "verbose_json",
            ct,
            prompt);
    }

    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderName => "Groq";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => IsConfigured;

    /// <summary>
    /// Gets the models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels =>
        _fetchedLlmModels.Count > 0
            ? _fetchedLlmModels.Select(m => new PluginModelInfo(m.Id, m.Id)).ToList()
            : FallbackLlmModels;

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("API key not configured");

        var modelId = ResolveLlmModelId(string.IsNullOrWhiteSpace(model) ? null : model);
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient, BaseUrl, _apiKey!, modelId, systemPrompt, userText, ct);
    }

    // API key management (for settings view)

    internal string? ApiKey => _apiKey;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal string? SelectedLlmModelId => _selectedLlmModelId;
    internal IReadOnlyList<FetchedLlmModel> FetchedLlmModels => _fetchedLlmModels;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        var wasConfigured = IsConfigured;
        var changed = !string.Equals(_apiKey, normalizedApiKey, StringComparison.Ordinal);

        _apiKey = normalizedApiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey);

            if (changed && wasConfigured != IsConfigured)
                _host.NotifyCapabilitiesChanged();
        }
    }

    internal void SelectLlmModel(string modelId)
    {
        _selectedLlmModelId = modelId;
        _host?.SetSetting("selectedLlmModel", modelId);
    }

    internal void SetFetchedLlmModels(List<FetchedLlmModel> models)
    {
        _fetchedLlmModels = NormalizeFetchedLlmModels(models);

        _host?.SetSetting("fetchedLlmModels", _fetchedLlmModels);
        NormalizeSelectedLlmModel();
        _host?.NotifyCapabilitiesChanged();
    }

    internal async Task<List<FetchedLlmModel>?> FetchLlmModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            return data.EnumerateArray()
                .Select(e => new FetchedLlmModel(
                    e.GetProperty("id").GetString() ?? "",
                    e.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null))
                .Where(m => IsLlmModel(m.Id))
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsLlmModel(string id)
    {
        var lowered = id.ToLowerInvariant();
        var excluded = new[]
        {
            "whisper",
            "distil-whisper",
            "tool-use",
            "orpheus",
            "tts",
            "prompt-guard",
            "safeguard",
        };

        return !excluded.Any(lowered.Contains);
    }

    internal string ResolveLlmModelId(string? requestedModel) =>
        !string.IsNullOrWhiteSpace(requestedModel)
            ? requestedModel
            : _selectedLlmModelId ?? SupportedModels.First().Id;

    private void NormalizeSelectedLlmModel()
    {
        var availableIds = new HashSet<string>(SupportedModels.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        if (_selectedLlmModelId is not null && availableIds.Contains(_selectedLlmModelId))
            return;

        _selectedLlmModelId = SupportedModels.FirstOrDefault()?.Id;
        if (_selectedLlmModelId is not null)
            _host?.SetSetting("selectedLlmModel", _selectedLlmModelId);
    }

    private static List<FetchedLlmModel> NormalizeFetchedLlmModels(IEnumerable<FetchedLlmModel> models) =>
        models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) && IsLlmModel(m.Id))
            .DistinctBy(m => m.Id)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static HttpClient CreateTranscriptionHttpClient(HttpMessageHandler? handler = null) =>
        CreateHttpClient(DefaultTranscriptionHttpTimeout, handler);

    private static HttpClient CreateHttpClient() => CreateHttpClient(DefaultHttpTimeout);

    private static HttpClient CreateHttpClient(TimeSpan timeout, HttpMessageHandler? handler = null) =>
        handler is null
            ? new HttpClient { Timeout = timeout }
            : new HttpClient(handler) { Timeout = timeout };

    private static GroqTranscriptionUpload CreateCompressedUpload(byte[] wavAudio)
    {
        if (wavAudio.Length == 0)
            throw new InvalidOperationException("No WAV audio bytes were provided.");

        using var input = new MemoryStream(wavAudio, writable: false);
        using var reader = new WaveFileReader(input);
        using var output = new MemoryStream();
        MediaFoundationEncoder.EncodeToAac(reader, output, TranscriptionUploadBitRate);

        var bytes = output.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("Media Foundation produced an empty AAC upload.");

        return new GroqTranscriptionUpload(bytes, "audio.m4a", "audio/mp4");
    }

    private static GroqTranscriptionUpload CreateWavUpload(byte[] wavAudio)
    {
        if (wavAudio.Length == 0)
            throw new InvalidOperationException("No WAV audio bytes were provided.");

        return new GroqTranscriptionUpload(wavAudio, "audio.wav", "audio/wav");
    }

    private static async Task<PluginTranscriptionResult> TranscribeUploadAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        GroqTranscriptionUpload upload,
        string? language,
        bool translate,
        string responseFormat,
        CancellationToken ct,
        string? prompt)
    {
        var endpoint = translate
            ? $"{baseUrl}/v1/audio/translations"
            : $"{baseUrl}/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(upload.Data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        content.Add(fileContent, "file", upload.FileName);
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(responseFormat), "response_format");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(new StringContent(prompt), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    private static InvalidOperationException CreateCompressionException(Exception ex) =>
        new(
            $"Groq audio upload could not be compressed before transcription: {ex.Message}",
            ex);

    private static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
        var language = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
        var duration = root.TryGetProperty("duration", out var durEl) ? durEl.GetDouble() : 0;

        var segments = new List<PluginTranscriptionSegment>();
        float? minNoSpeechProb = null;
        if (root.TryGetProperty("segments", out var segmentsEl)
            && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                var segmentText = seg.TryGetProperty("text", out var segTextEl)
                    ? segTextEl.GetString() ?? ""
                    : "";
                var start = seg.TryGetProperty("start", out var startEl)
                    ? startEl.GetDouble()
                    : 0;
                var end = seg.TryGetProperty("end", out var endEl)
                    ? endEl.GetDouble()
                    : 0;
                segments.Add(new PluginTranscriptionSegment(segmentText, start, end));

                if (seg.TryGetProperty("no_speech_prob", out var nspEl))
                {
                    var prob = (float)nspEl.GetDouble();
                    minNoSpeechProb = minNoSpeechProb is null
                        ? prob
                        : Math.Min(minNoSpeechProb.Value, prob);
                }
            }
        }

        return new PluginTranscriptionResult(text.Trim(), language, duration, minNoSpeechProb)
        {
            Segments = segments
        };
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        if (!ReferenceEquals(_httpClient, _transcriptionHttpClient))
            _transcriptionHttpClient.Dispose();
    }

    private sealed record TranscriptionModelEntry(
        string Id, string DisplayName, string ApiModelName, bool SupportsTranslation);
}

internal sealed record GroqTranscriptionUpload(byte[] Data, string FileName, string ContentType);

internal sealed record FetchedLlmModel(string Id, string? OwnedBy);
