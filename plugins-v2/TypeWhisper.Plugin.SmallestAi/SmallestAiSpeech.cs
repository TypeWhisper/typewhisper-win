using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SmallestAi;

public sealed partial class SmallestAiPlugin : ITtsProviderPlugin, IPluginTextSettings, IPluginSettingsActions
{
    internal sealed record SpeechVoice(string Id, string Name, string Model, string? Language);
    private SpeechVoice[] _voices = [new("magnus", "Magnus", "lightning_v3.1", "en")];
    private string? _selectedVoiceId;
    private double _speed = 1;
    private static string VoiceKey(SpeechVoice voice) => voice.Model + ":" + voice.Id;
    /// <inheritdoc />
    public bool SupportsPlaybackSelection => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => _voices.Select(v => new PluginVoiceInfo(
        VoiceKey(v), $"{v.Name} · {(v.Model.EndsWith("_pro", StringComparison.Ordinal) ? "Lightning Pro" : "Lightning")}{(v.Language is null ? "" : " · " + v.Language)}", v.Language)).ToArray();
    /// <inheritdoc />
    public string? SelectedVoiceId => _selectedVoiceId ?? VoiceKey(_voices[0]);
    /// <inheritdoc />
    public string? SettingsSummary => $"Lightning · {_speed.ToString("0.##", CultureInfo.InvariantCulture)}×";

    private void RestoreSpeechSettings(IPluginHostServices host)
    {
        var cached = host.GetSetting<SpeechVoice[]>("speech-voices");
        if (cached is { Length: > 0 } && cached.All(IsValidVoice)) _voices = cached;
        var selected = host.GetSetting<string>("speech-voice");
        _selectedVoiceId = _voices.Any(v => VoiceKey(v) == selected) ? selected : null;
        var speed = host.GetSetting<double?>("speech-speed");
        _speed = speed is >= 0.5 and <= 2 ? speed.Value : 1;
    }

    /// <inheritdoc />
    public void SelectVoice(string? voiceId)
    {
        if (voiceId is not null && !_voices.Any(v => VoiceKey(v) == voiceId))
            throw new ArgumentException("The requested Smallest AI voice is unavailable.", nameof(voiceId));
        var host = _host ?? throw new InvalidOperationException("Plugin is not active.");
        host.SetSetting("speech-voice", voiceId);
        _selectedVoiceId = voiceId;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("speech-speed", "Speech speed", "Playback speed from 0.5 to 2. Voices are selected under Audio → Spoken feedback.", _speed.ToString("0.##", CultureInfo.InvariantCulture), 8)
        { Section = PluginSettingsSection.Speech }
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id != "speech-speed") throw new ArgumentException("Unknown setting.", nameof(id));
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var speed) || speed is < 0.5 or > 2 || !double.IsFinite(speed))
            throw new ArgumentException("Speech speed must be between 0.5 and 2.", nameof(value));
        (_host ?? throw new InvalidOperationException("Plugin is not active.")).SetSetting(id, speed);
        _speed = speed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [new("refresh-voices", "Refresh voices", "Load the current Lightning and Lightning Pro voices from Smallest AI.") { Section = PluginSettingsSection.Speech }];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        if (id != "refresh-voices") throw new ArgumentException("Unknown action.", nameof(id));
        await RefreshVoicesAsync(cancellationToken);
        return $"{_voices.Length} voices available under Audio → Spoken feedback.";
    }

    internal async Task RefreshVoicesAsync(CancellationToken ct)
    {
        var host = _host ?? throw new InvalidOperationException("Plugin is not active.");
        var key = _apiKey;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("API key required.");
        var voices = new List<SpeechVoice>();
        foreach (var model in new[] { "lightning_v3.1", "lightning_v3.1_pro" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/waves/v1/{model.Replace('_', '-')}/get_voices");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
            voices.AddRange(ParseVoices(await response.Content.ReadAsStringAsync(ct), model));
        }
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(host, _host) || key != _apiKey) throw new InvalidOperationException("The connection changed. Refresh voices again.");
        var snapshot = voices.DistinctBy(VoiceKey).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (snapshot.Length == 0) throw new InvalidDataException("Smallest AI returned no voices.");
        host.SetSetting("speech-voices", snapshot);
        _voices = snapshot;
        if (!_voices.Any(v => VoiceKey(v) == _selectedVoiceId)) _selectedVoiceId = null;
        host.NotifyCapabilitiesChanged();
    }

    internal static SpeechVoice[] ParseVoices(string json, string model)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Smallest AI returned an invalid voice catalog.");
        return voices.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).Select(v =>
        {
            string? language = null;
            if (v.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object && (tags.TryGetProperty("recommendedLanguages", out var languages) || tags.TryGetProperty("language", out languages)) && languages.ValueKind == JsonValueKind.Array)
                language = languages.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => LanguageCode(l.GetString())).FirstOrDefault(l => l is not null);
            return new SpeechVoice(GetString(v, "voiceId") ?? "", GetString(v, "displayName") ?? "", model, language);
        }).Where(IsValidVoice).ToArray();
    }

    private static bool IsValidVoice(SpeechVoice v) => v is not null && !string.IsNullOrWhiteSpace(v.Id) && !string.IsNullOrWhiteSpace(v.Name) && v.Model is "lightning_v3.1" or "lightning_v3.1_pro";
    private static string? LanguageCode(string? value) => value?.ToLowerInvariant() switch
    {
        "english" => "en", "german" => "de", "french" => "fr", "spanish" => "es", "italian" => "it", "hindi" => "hi",
        "portuguese" => "pt", "dutch" => "nl", "swedish" => "sv", "polish" => "pl", "russian" => "ru", "greek" => "el", "finnish" => "fi", "norwegian" => "no",
        "marathi" => "mr", "gujarati" => "gu", "punjabi" => "pa", "bengali" => "bn", "odia" => "or", "tamil" => "ta", "telugu" => "te", "kannada" => "kn", "malayalam" => "ml",
        "chinese" => "zh", "japanese" => "ja", "korean" => "ko", "indonesian" => "id", "malay" => "ms", "vietnamese" => "vi", "turkish" => "tr", "arabic" => "ar",
        { Length: 2 } code => code, _ => null
    };

    internal Func<byte[], string?, ITtsPlaybackSession> PlaybackFactory { get; set; } = (audio, device) => new SmallestAiPlaybackSession(audio, device);

    /// <inheritdoc />
    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("API key required.");
        ct.ThrowIfCancellationRequested();
        var text = request.Text.Trim();
        if (text.Length is 0 or > 8000) throw new ArgumentException("Speech text must contain between 1 and 8000 characters.", nameof(request));
        var id = request.VoiceId ?? SelectedVoiceId;
        var voice = _voices.FirstOrDefault(v => VoiceKey(v) == id) ?? throw new ArgumentException("The requested Smallest AI voice is unavailable.", nameof(request));
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/waves/v1/tts");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        message.Content = JsonContent.Create(new { text, voice_id = voice.Id, model = voice.Model, sample_rate = 24000, output_format = "wav", speed = _speed,
            language = NormalizeLanguage(request.Language)?.Split('-', '_')[0].ToLowerInvariant() ?? "auto" });
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, message, ct);
        var audio = await response.Content.ReadAsByteArrayAsync(ct);
        if (audio.Length < 44 || !audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !audio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Smallest AI returned no playable WAV audio.");
        ct.ThrowIfCancellationRequested();
        return PlaybackFactory(audio, request.OutputDeviceId);
    }
}
