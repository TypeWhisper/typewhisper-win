using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Cloud;

public abstract class CloudProviderBase : ITranscriptionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private string? _currentApiModelName;
    private string _responseFormat = "verbose_json";
    private bool _disposed;

    protected CloudProviderBase() : this(new HttpClient(), ownsClient: true) { }

    protected CloudProviderBase(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsClient;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string BaseUrl { get; }
    public abstract IReadOnlyList<CloudModelInfo> TranscriptionModels { get; }
    public abstract string? TranslationModel { get; }

    public string? ApiKey { get; private set; }
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);
    public bool SupportsTranslation => TranslationModel is not null && IsConfigured;

    public void Configure(string apiKey) => ApiKey = apiKey;

    public void SelectTranscriptionModel(string modelId)
    {
        var model = TranscriptionModels.FirstOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentException($"Unbekanntes Modell: {modelId} für Provider {Id}");
        _currentApiModelName = model.ApiModelName;
        _responseFormat = model.ResponseFormat;
    }

    // ITranscriptionEngine

    public bool IsModelLoaded => IsConfigured && _currentApiModelName is not null;

    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void UnloadModel() => _currentApiModelName = null;

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        if (!IsModelLoaded)
            throw new InvalidOperationException("Cloud-Engine nicht konfiguriert. API-Key und Modell erforderlich.");

        var wavBytes = WavEncoder.Encode(audioSamples);

        var endpoint = task == TranscriptionTask.Translate
            ? $"{BaseUrl}/v1/audio/translations"
            : $"{BaseUrl}/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(_currentApiModelName!), "model");
        content.Add(new StringContent(_responseFormat), "response_format");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = content;

        var response = await SendWithErrorHandling(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseTranscriptionResponse(json);
    }

    // LLM Translation via chat completion

    public async Task<string> TranslateAsync(
        string text, string sourceLang, string targetLang,
        CancellationToken cancellationToken = default)
    {
        if (TranslationModel is null)
            throw new NotSupportedException($"Provider {Id} unterstützt keine LLM-Übersetzung");
        if (!IsConfigured)
            throw new InvalidOperationException("API-Key nicht konfiguriert");

        var requestBody = JsonSerializer.Serialize(new
        {
            model = TranslationModel,
            messages = new object[]
            {
                new { role = "system", content = "You are a professional translator. Translate the given text accurately and naturally. Output ONLY the translation, nothing else. Do not add explanations, notes, or formatting." },
                new { role = "user", content = $"Translate from {sourceLang} to {targetLang}:\n\n{text}" }
            },
            temperature = 0.1,
            max_tokens = 2048
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await SendWithErrorHandling(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseChatCompletionResponse(json);
    }

    // API key validation

    public async Task<bool> ValidateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Shared HTTP + parsing

    private async Task<HttpResponseMessage> SendWithErrorHandling(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Netzwerkfehler: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Zeitüberschreitung bei der API-Anfrage.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = (int)response.StatusCode switch
            {
                401 => "Ungültiger API-Key",
                413 => "Audio zu groß (max 25 MB)",
                429 => "Rate-Limit erreicht, bitte warten",
                _ => $"API-Fehler {(int)response.StatusCode}: {ExtractErrorMessage(errorBody)}"
            };
            throw new InvalidOperationException(message);
        }

        return response;
    }

    private static TranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
        var language = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
        var duration = root.TryGetProperty("duration", out var durEl) ? durEl.GetDouble() : 0;

        return new TranscriptionResult
        {
            Text = text.Trim(),
            DetectedLanguage = language,
            Duration = duration
        };
    }

    private static string ParseChatCompletionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString()?.Trim() ?? "";
            }
        }

        return "";
    }

    private static string ExtractErrorMessage(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                if (errorEl.ValueKind == JsonValueKind.Object && errorEl.TryGetProperty("message", out var msgEl))
                    return msgEl.GetString() ?? errorBody;
                if (errorEl.ValueKind == JsonValueKind.String)
                    return errorEl.GetString() ?? errorBody;
            }
        }
        catch { }
        return errorBody.Length > 200 ? errorBody[..200] : errorBody;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsHttpClient) _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
