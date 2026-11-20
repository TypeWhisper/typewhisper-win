using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Zoom;

/// <summary>Zoom Scribe transcription: the Fast mode for recordings and files, the Live mode for dictations.</summary>
public sealed partial class ZoomPlugin : ITranscriptionEnginePlugin, IApiKeyPlugin,
    IPluginProfileSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    internal const string FastUrl = "https://api.zoom.us/v2/aiservices/scribe/transcribe";

    /// <summary>The Fast mode takes at most 2 MB of request body.</summary>
    internal const int MaximumBodyBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 45 seconds of 16 kHz PCM16 WAV, about 1.9 MB in base64, so a request stays under <see cref="MaximumBodyBytes"/> with
    /// 100 dictionary terms. The host splits longer audio at quiet points. macOS sends three-minute M4A pieces and the same
    /// 45-second WAV pieces when its M4A encoding fails.
    /// </summary>
    internal const int MaximumWavBytes = 44 + 45 * 16_000 * 2;

    private readonly HttpClient _http;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private Configuration _configuration = new(ZoomModels.Default.Id, ZoomModes.Live, "api-key");
    private sealed record Configuration(string Model, string? Mode, string? SecretName);

    /// <summary>Creates the provider with its own HTTP transport.</summary>
    // Each request sets its own deadline in ReadAsync.
    public ZoomPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }) { }
    internal ZoomPlugin(HttpClient http, Func<string, ZoomRequest, CancellationToken, Task<IStreamingSession>>? connectLive = null)
    {
        _http = http;
        _connectLive = connectLive ?? ZoomStreamingSession.ConnectAsync;
    }

    private readonly Func<string, ZoomRequest, CancellationToken, Task<IStreamingSession>> _connectLive;

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.zoom";
    /// <inheritdoc />
    public string PluginName => "Zoom Scribe";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => "zoom";
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public bool IsConfigured => _host is not null && !string.IsNullOrWhiteSpace(_apiKey);
    /// <inheritdoc />
    public int MaximumAudioUploadBytes => MaximumWavBytes;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = ZoomModels.All.Select(m =>
        new PluginModelInfo(m.Id, m.Name)
        {
            LanguageCodes = m.Languages, LanguageCount = m.Languages.Count, IsRecommended = m.Id == ZoomModels.Default.Id
        }).ToArray();
    /// <inheritdoc />
    public string? SelectedModelId => _configuration.Model;
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    /// <remarks>Only the Live mode streams; After recording shows no live preview from Zoom, so it costs what the recording costs.</remarks>
    public bool SupportsStreaming => Mode == ZoomModes.Live;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsStructuredDictionaryTerms => true;
    /// <inheritdoc />
    /// <remarks>Live sessions take the same vocabulary as the Fast mode, so a dictation streams with its terms.</remarks>
    public bool SupportsDictionaryTerms => Model.UsesVocabulary;
    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => ZoomModels.Budget;
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => Model.Languages;
    private ZoomModel Model => ZoomModels.Resolve(_configuration.Model);
    internal string Mode => ZoomModes.Normalize(_configuration.Mode);

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<Configuration>("configuration") ?? new(ZoomModels.Default.Id, ZoomModes.Live, "api-key");
        var normalized = saved with { Model = ZoomModels.Resolve(saved.Model).Id, Mode = ZoomModes.Normalize(saved.Mode) };
        var key = normalized.SecretName is null ? null : await host.LoadSecretAsync(normalized.SecretName);
        if (saved != normalized) host.SetSetting("configuration", normalized);
        _configuration = normalized;
        _apiKey = NormalizeKey(key);
        _host = host;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() { _host = null; _apiKey = null; return Task.CompletedTask; }
    /// <inheritdoc />
    public void SelectModel(string modelId) => Commit(_configuration with { Model = ZoomModels.Require(modelId).Id });

    /// <summary>The request for <paramref name="language"/> with the selected model.</summary>
    internal ZoomRequest RequestFor(string? language, string? prompt) => ZoomRequest.For(Model, language, prompt);

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) => StartStreamAsync(language, null, ct);
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct) =>
        StartStreamAsync(languageHints.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)), prompt, ct);

    private Task<IStreamingSession> StartStreamAsync(string? language, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = RequireKey();
        if (!SupportsStreaming) throw new NotSupportedException("Live transcription is off in the Zoom Scribe settings.");
        return _connectLive(key, RequestFor(language, prompt), ct);
    }

    /// <inheritdoc />
    /// <remarks>Recordings, files and dictations without a live session use the Fast mode.</remarks>
    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = RequireKey();
        if (translate) throw new NotSupportedException("Zoom Scribe does not support native translation.");
        if (wavAudio.Length == 0) throw new ArgumentException("Audio is empty.", nameof(wavAudio));
        var transcription = RequestFor(language, prompt);
        var body = FastBody(wavAudio, transcription);
        if (body.Length > MaximumBodyBytes)
            throw new PluginRequestException("The audio is too large for one Zoom Scribe request.", PluginRequestFailureKind.RequestTooLarge, 413);

        var started = Stopwatch.StartNew();
        using var request = Request(key, body);
        using var response = await ReadAsync(request, ct);
        var result = ParseFastResult(response.RootElement, transcription);
        _host?.Log(PluginLogLevel.Info, $"Zoom Scribe returned the transcript with {transcription.Model} after {started.ElapsedMilliseconds} ms.");
        return result;
    }

    /// <summary>The Fast mode's JSON body: the audio inline as a WAV data URL, and the request's <c>config</c>.</summary>
    internal static byte[] FastBody(byte[] wavAudio, ZoomRequest transcription)
    {
        using var stream = new MemoryStream(wavAudio.Length / 3 * 4 + 4096);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("file", "data:audio/wav;base64," + Convert.ToBase64String(wavAudio));
            writer.WritePropertyName("config");
            JsonSerializer.Serialize(writer, transcription.Config());
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// The transcript of a Fast response: <c>result.text_display</c>, or the segment texts. <c>result.language</c> or the
    /// segments' language is the detected language; a request with a fixed language reports none.
    /// </summary>
    internal static PluginTranscriptionResult ParseFastResult(JsonElement root, ZoomRequest transcription)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var segments = new List<PluginTranscriptionSegment>();
        var texts = new List<string>();
        var leading = new List<string>();
        string? segmentLanguage = null;
        if (result.TryGetProperty("segments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in list.EnumerateArray())
            {
                var text = OptionalString(segment, "text")?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                texts.Add(text);
                segmentLanguage ??= NonEmpty(OptionalString(segment, "language"));
                // A segment without times joins the one before it, or the next one at the start, so subtitles keep all text.
                if (Number(segment, "start") is { } start && Number(segment, "end") is { } end && end >= start)
                {
                    segments.Add(new(ZoomStreamingSession.JoinedText(leading.Append(text)), start, end));
                    leading.Clear();
                }
                else if (segments.Count > 0) segments[^1] = segments[^1] with { Text = ZoomStreamingSession.JoinedText([segments[^1].Text, text]) };
                else leading.Add(text);
            }
        }
        // Zoom writes Chinese and Japanese without spaces, so the segments join like live items.
        var transcript = OptionalString(result, "text_display") ?? ZoomStreamingSession.JoinedText(texts);
        var language = NonEmpty(OptionalString(result, "language")) ?? segmentLanguage;
        var duration = Number(root, "duration_sec") ?? (segments.Count > 0 ? segments.Max(s => s.End) : 0);
        return new(transcript.Trim(), language is null ? transcription.LanguageCode : ZoomModels.LanguageCode(language), duration,
            NoSpeechProbability: null) { Segments = segments };
    }

    private static HttpRequestMessage Request(string key, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, FastUrl) { Content = new ByteArrayContent(body) };
        request.Headers.Add("Authorization", "Bearer " + key);
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        return request;
    }

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Sends <paramref name="request"/> with a deadline of <paramref name="timeout"/>, 120 s by default; a missed deadline is a timeout failure.</summary>
    private async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, deadline.Token);
            var json = await response.Content.ReadAsStringAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode) throw Rejected((int)response.StatusCode, json, response.Headers.RetryAfter?.Delta);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw InvalidResponse(); }
            return doc;
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("Zoom Scribe request timed out.", PluginRequestFailureKind.Timeout); }
        catch (HttpRequestException) { throw new PluginRequestException("Could not reach Zoom Scribe.", PluginRequestFailureKind.Network); }
    }

    /// <summary>
    /// The failure for a rejected request. Zoom's errors carry a <c>reason</c> such as <c>UNSUPPORTED_MODEL</c>, which the
    /// message includes; Zoom's free-text message is left out, so no request content reaches logs.
    /// </summary>
    internal static PluginRequestException Rejected(int status, string body, TimeSpan? retryAfter = null)
    {
        var kind = status switch
        {
            401 => PluginRequestFailureKind.Authentication, 403 => PluginRequestFailureKind.Permission,
            402 or 429 => PluginRequestFailureKind.RateLimit, 408 => PluginRequestFailureKind.Timeout,
            413 => PluginRequestFailureKind.RequestTooLarge, >= 500 => PluginRequestFailureKind.ServerError,
            _ => PluginRequestFailureKind.InvalidRequest
        };
        var reason = Reason(body);
        var message = status == 402
            ? "Zoom Scribe declined the request; the account may be out of Build Credits."
            : "Zoom Scribe rejected the request" + (reason is null ? "." : $" ({reason}).");
        return new(message, kind, status, retryAfter);
    }

    /// <summary>The <c>reason</c> or <c>code</c> of a Zoom error body, when it is an identifier.</summary>
    internal static string? Reason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "reason", "code" })
            {
                if (!root.TryGetProperty(name, out var field)) continue;
                var value = field.ValueKind switch
                {
                    JsonValueKind.String => field.GetString(), JsonValueKind.Number => field.GetRawText(), _ => null
                };
                if (value is not null && SafeReason().IsMatch(value)) return value;
            }
        }
        catch (JsonException) { }
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex SafeReason();

    private string RequireKey() => IsConfigured ? _apiKey! : throw new PluginRequestException("Configure a Zoom Scribe API key first.", PluginRequestFailureKind.Configuration);
    internal static PluginRequestException InvalidResponse() => new("Zoom Scribe returned an invalid or incomplete response.", PluginRequestFailureKind.OutputIncomplete);
    internal static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number
        && field.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0 ? value : null;
    /// <inheritdoc />
    public void Dispose() { _apiKey = null; _host = null; _http.Dispose(); }
}
