using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Soniox;

public sealed partial class SonioxPlugin : ITtsProviderPlugin
{
    internal const string TtsModelSettingKey = "tts-model";
    internal const string VoiceSettingKey = "speech-voice";
    internal const string DefaultVoiceId = "Adrian";
    internal const int SpeechSampleRate = 24_000;
    internal const int MaximumSpeechTextCharacters = 5_000;
    // Soniox caps generated audio at two minutes per request (24 kHz mono PCM16), plus some headroom.
    internal const int MaximumSpeechAudioBytes = 2 * 60 * SpeechSampleRate * 2 + 64 * 1024;
    private const int MaximumSpeechErrorBytes = 64 * 1024;

    // Voices offered before the model list is loaded; all of them are listed for tts-rt-v2.
    private static readonly string[] FallbackVoices =
    [
        "Adrian", "Arthur", "Daniel", "Emma", "Grace", "Isla", "Kenji", "Lucia", "Mateo", "Mina",
        "Nina", "Oliver", "Owen", "Priya", "Rafael", "Ruby", "Victoria",
    ];

    // Languages tts-rt-v2 speaks, used before the model list is loaded.
    private static readonly string[] FallbackSpeechLanguages =
    [
        "af", "ar", "az", "be", "bg", "bn", "bs", "ca", "cs", "cy", "da", "de", "el", "en", "es", "et",
        "eu", "fa", "fi", "fr", "gl", "gu", "he", "hi", "hr", "hu", "id", "is", "it", "ja", "kk", "kn",
        "ko", "lt", "lv", "mk", "ml", "mr", "ms", "nl", "no", "pa", "pl", "pt", "ro", "ru", "sk", "sl",
        "sq", "sr", "su", "sv", "sw", "ta", "te", "th", "tl", "tr", "uk", "ur", "uz", "vi", "zh",
    ];

    private string _ttsModelSelection = SonioxModels.Automatic;
    private string? _voiceId;

    /// <summary>Plays through the output device and voice the host chooses for each request.</summary>
    public bool SupportsPlaybackSelection => true;

    /// <inheritdoc />
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices =>
        SpeechVoices().Select(voice => new PluginVoiceInfo(voice, voice)).ToArray();

    /// <inheritdoc />
    public string? SelectedVoiceId
    {
        get
        {
            var voices = SpeechVoices();
            if (_voiceId is not null && voices.Contains(_voiceId, StringComparer.Ordinal))
                return _voiceId;
            return voices.Contains(DefaultVoiceId, StringComparer.Ordinal) ? DefaultVoiceId : voices.FirstOrDefault();
        }
    }

    /// <inheritdoc />
    public string? SettingsSummary => $"{RegionTitle(ResolveRegion(_region))} · {SelectedVoiceId}";

    internal string TtsModelSelection => _ttsModelSelection;

    /// <summary>The text-to-speech model used with the current selection.</summary>
    internal string EffectiveTtsModelId => _ttsModelSelection == SonioxModels.Automatic
        ? SonioxModels.PreferredTtsModelId(CurrentCatalog?.TtsModels)
        : _ttsModelSelection;

    /// <inheritdoc />
    public void SelectVoice(string? voiceId)
    {
        var trimmed = string.IsNullOrWhiteSpace(voiceId) ? null : voiceId.Trim();
        if (trimmed is not null && !SpeechVoices().Contains(trimmed, StringComparer.Ordinal))
            throw new ArgumentException("The requested Soniox voice is unavailable.", nameof(voiceId));
        _host?.SetSetting(VoiceSettingKey, trimmed);
        _voiceId = trimmed;
    }

    internal void SelectTtsModel(string selection)
    {
        var trimmed = selection.Trim();
        if (trimmed != SonioxModels.Automatic && !SonioxModels.IsValidModelId(trimmed))
            throw new ArgumentException("Unknown Soniox speech model.", nameof(selection));
        _host?.SetSetting(TtsModelSettingKey, trimmed);
        _ttsModelSelection = trimmed;
    }

    private void RestoreSpeechSettings(IPluginHostServices host)
    {
        var model = ReadSetting<string>(host, TtsModelSettingKey)?.Trim();
        _ttsModelSelection = SonioxModels.IsValidModelId(model) ? model! : SonioxModels.Automatic;
        var voice = ReadSetting<string>(host, VoiceSettingKey)?.Trim();
        _voiceId = SonioxModels.IsValidVoiceId(voice) ? voice : null;
    }

    internal IReadOnlyList<string> SpeechVoices() =>
        EffectiveTtsModel() is { Voices.Count: > 0 } model ? model.Voices : FallbackVoices;

    private IReadOnlyList<string> SpeechLanguages() =>
        EffectiveTtsModel() is { Languages.Count: > 0 } model ? model.Languages : FallbackSpeechLanguages;

    // An alias such as tts-rt-v1 lists no voices of its own; its target model provides them.
    private SonioxTtsModel? EffectiveTtsModel()
    {
        var models = CurrentCatalog?.TtsModels;
        var modelId = EffectiveTtsModelId;
        var model = models?.FirstOrDefault(candidate => candidate.Id == modelId);
        return model?.AliasedModelId is { } target
            ? models!.FirstOrDefault(candidate => candidate.Id == target) ?? model
            : model;
    }

    /// <summary>
    /// Soniox requires a language. The request's language wins; without one, the app language is the best guess
    /// for spoken feedback, and English is the last resort.
    /// </summary>
    internal string ResolveSpeechLanguage(string? requested)
    {
        var supported = SpeechLanguages();
        string? appLanguage;
        try { appLanguage = PortableLocalization.TryGet(_host)?.CurrentLanguage; }
        catch (InvalidOperationException) { appLanguage = null; }

        foreach (var candidate in new[] { requested, appLanguage, CultureInfo.CurrentUICulture.Name })
        {
            var code = candidate?.Trim();
            if (string.IsNullOrEmpty(code) || code.Equals("auto", StringComparison.OrdinalIgnoreCase))
                continue;
            var primary = code.Split('-', '_')[0];
            if (SpeechLanguageAliases.TryGetValue(primary, out var alias))
                primary = alias;
            var match = supported.FirstOrDefault(language => language.Equals(code, StringComparison.OrdinalIgnoreCase))
                ?? supported.FirstOrDefault(language => language.Equals(primary, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return "en";
    }

    // Windows names Norwegian nb/nn and Filipino fil; Soniox lists them as no and tl.
    private static readonly Dictionary<string, string> SpeechLanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nb"] = "no",
        ["nn"] = "no",
        ["fil"] = "tl",
    };

    internal static Dictionary<string, object> CreateSpeechRequestBody(string text, string voice, string language, string modelId) => new(StringComparer.Ordinal)
    {
        ["model"] = modelId,
        ["language"] = language,
        ["voice"] = voice,
        ["audio_format"] = "pcm_s16le",
        ["sample_rate"] = SpeechSampleRate,
        ["text"] = text,
    };

    internal Func<byte[], string?, ITtsPlaybackSession> SpeechPlaybackFactory { get; set; } = (pcm, outputDeviceId) =>
        OperatingSystem.IsWindows()
            ? new SonioxSpeechPlaybackSession(pcm, SpeechSampleRate, outputDeviceId)
            : throw new PlatformNotSupportedException("Speech playback requires Windows.");

    /// <inheritdoc />
    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = _apiKey;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Soniox API key required.");
        ct.ThrowIfCancellationRequested();

        var text = request.Text?.Trim() ?? "";
        if (text.Length == 0)
            return SonioxInactivePlaybackSession.Instance;
        if (text.Length > MaximumSpeechTextCharacters)
            throw new ArgumentException("Speech text must not exceed 5,000 characters.", nameof(request));

        // Spoken feedback alone keeps the voices current. The refresh starts before the voice check, so a voice
        // missing from a stale list becomes available for the next request; this request uses the current list.
        RefreshCatalogInBackgroundIfStale();
        var voice = string.IsNullOrWhiteSpace(request.VoiceId) ? SelectedVoiceId ?? DefaultVoiceId : request.VoiceId.Trim();
        if (!SpeechVoices().Contains(voice, StringComparer.Ordinal))
            throw new ArgumentException("The requested Soniox voice is unavailable.", nameof(request));

        var body = CreateSpeechRequestBody(text, voice, ResolveSpeechLanguage(request.Language), EffectiveTtsModelId);
        using var message = new HttpRequestMessage(HttpMethod.Post, ResolveRegion(_region).TtsUrl);
        AddAuthorization(message, key);
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedAsync(response.Content, MaximumSpeechErrorBytes, truncate: true, ct).ConfigureAwait(false);
            var error = ExtractApiErrorDetails(Encoding.UTF8.GetString(errorBody));
            throw new HttpRequestException(
                $"Soniox speech error {(int)response.StatusCode}: {FormatApiError(error)}", inner: null, response.StatusCode);
        }

        var pcm = await ReadBoundedAsync(response.Content, MaximumSpeechAudioBytes, truncate: false, ct).ConfigureAwait(false);
        if (pcm.Length == 0 || pcm.Length % sizeof(short) != 0)
            throw new InvalidDataException("Soniox returned no playable speech audio.");
        ct.ThrowIfCancellationRequested();
        return SpeechPlaybackFactory(pcm, request.OutputDeviceId);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, bool truncate, CancellationToken ct)
    {
        if (!truncate && content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Soniox speech audio exceeds the two-minute limit.");

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                if (!truncate)
                    throw new InvalidDataException("Soniox speech audio exceeds the two-minute limit.");
                buffer.Write(chunk, 0, (int)(maximumBytes - buffer.Length));
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
