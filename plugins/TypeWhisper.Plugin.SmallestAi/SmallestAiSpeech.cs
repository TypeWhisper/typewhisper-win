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
    // Keep aligned with the host spoken-feedback audio contract.
    internal const int MaximumSpeechAudioBytes = 12 * 1024 * 1024;
    internal const int MaximumSpeechAudioSeconds = 120;
    internal sealed record SpeechVoice(string Id, string Name, string Model, string? Language);
    private const string VoicesSetting = "speech-voices";
    private const string VoiceSetting = "speech-voice";
    internal const string VoicesFetchedAtSetting = "speech-voices-fetched-at";
    private const string StandardModel = "lightning_v3.1";
    /// <summary>The built-in voice, used until the catalogs are loaded and while the saved voice is not offered.</summary>
    internal const string DefaultVoiceKey = StandardModel + ":magnus";
    private SpeechVoice[] _voices = [new("magnus", "Magnus", StandardModel, "en")];
    private DateTimeOffset? _voicesFetchedAt;
    // The saved voice. It stays saved while the catalogs lack it and applies again once they offer it.
    private string? _preferredVoiceId;
    private double _speed = 1;
    private static string VoiceKey(SpeechVoice voice) => voice.Model + ":" + voice.Id;
    /// <inheritdoc />
    public bool SupportsPlaybackSelection => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => _voices.Select(v => new PluginVoiceInfo(
        VoiceKey(v), $"{v.Name} · {(v.Model.EndsWith("_pro", StringComparison.Ordinal) ? "Lightning Pro" : "Lightning")}{(v.Language is null ? "" : " · " + v.Language)}", v.Language)).ToArray();
    /// <inheritdoc />
    public string? SelectedVoiceId
    {
        get
        {
            var voices = _voices;
            var preferred = _preferredVoiceId;
            return voices.Any(v => VoiceKey(v) == preferred) ? preferred : DefaultVoice(voices);
        }
    }
    /// <inheritdoc />
    public string? SettingsSummary => $"Lightning · {_speed.ToString("0.##", CultureInfo.InvariantCulture)}×";

    internal DateTimeOffset? VoicesFetchedAt => _voicesFetchedAt;
    /// <summary>The running background refresh, or the last one. Tests await it.</summary>
    internal Task VoiceRefreshTask => _voiceRefresh.Current;

    // The catalogs are sorted by name, so their first voice changes whenever Smallest AI adds one. Magnus stays the
    // default; without it a standard Lightning voice comes before a Pro one.
    private static string DefaultVoice(SpeechVoice[] voices) => VoiceKey(
        voices.FirstOrDefault(v => VoiceKey(v) == DefaultVoiceKey) ?? voices.FirstOrDefault(v => v.Model == StandardModel) ?? voices[0]);

    private void RestoreSpeechSettings(IPluginHostServices host)
    {
        var cached = host.GetSetting<SpeechVoice[]>(VoicesSetting);
        var restored = cached is { Length: > 0 } && cached.All(IsValidVoice);
        if (restored) _voices = cached!;
        // Catalogs saved before 1.3.0 have no date; they stay until a refresh replaces them.
        _voicesFetchedAt = restored ? ReadFetchedAt(host) : null;
        _preferredVoiceId = host.GetSetting<string>(VoiceSetting);
        var speed = host.GetSetting<double?>("speech-speed");
        _speed = speed is >= 0.5 and <= 2 ? speed.Value : 1;
    }

    private static DateTimeOffset? ReadFetchedAt(IPluginHostServices host)
    {
        try { return host.GetSetting<DateTimeOffset?>(VoicesFetchedAtSetting); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <inheritdoc />
    public void SelectVoice(string? voiceId)
    {
        if (voiceId is not null && !_voices.Any(v => VoiceKey(v) == voiceId))
            throw new ArgumentException("The requested Smallest AI voice is unavailable.", nameof(voiceId));
        var host = _host ?? throw new InvalidOperationException("Plugin is not active.");
        host.SetSetting(VoiceSetting, voiceId);
        _preferredVoiceId = voiceId;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("speech-speed", "Speech speed", "Playback speed from 0.5 to 2. Voices are selected under Advanced → Spoken feedback.", _speed.ToString("0.##", CultureInfo.InvariantCulture), 8)
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
        return $"{_voices.Length} voices available under Advanced → Spoken feedback.";
    }

    internal async Task RefreshVoicesAsync(CancellationToken ct)
    {
        var host = _host ?? throw new InvalidOperationException("Plugin is not active.");
        var key = _apiKey;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("API key required.");
        // The button loads the voices itself; a background refresh would only repeat the requests.
        _voiceRefresh.Cancel();
        var snapshot = await FetchVoicesAsync(key, ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(host, _host) || key != _apiKey) throw ConnectionChanged();
        if (snapshot.Length == 0) throw new InvalidDataException("Smallest AI returned no voices.");
        if (!TryApplyVoices(host, key, snapshot, CancellationToken.None)) throw ConnectionChanged();
    }

    private static InvalidOperationException ConnectionChanged() => new("The connection changed. Refresh voices again.");

    /// <summary>
    /// Smallest AI adds and retires voices, and a list that only reloads on request keeps offering retired ones. With a
    /// key, the catalogs reload in the background once the saved copy is a day old or has no date.
    /// </summary>
    private void StartVoiceRefreshIfStale()
    {
        lock (_voiceLock)
        {
            if (!_voiceRefresh.Enabled || _host is not { } host || _apiKey is not { Length: > 0 } key || !_voiceRefresh.IsStale(_voicesFetchedAt))
                return;
            host.Log(PluginLogLevel.Info, "Refreshing the Smallest AI voices in the background.");
            _voiceRefresh.Start(ct => RefreshVoicesInBackgroundAsync(host, key, ct),
                ex => host.Log(PluginLogLevel.Warning, $"The Smallest AI voices could not be refreshed ({Describe(ex)}). Keeping the current voices."));
        }
    }

    private async Task RefreshVoicesInBackgroundAsync(IPluginHostServices host, string key, CancellationToken ct)
    {
        var snapshot = await FetchVoicesAsync(key, ct);
        // An empty answer would leave only the built-in voice.
        if (snapshot.Length == 0) throw new InvalidDataException("Smallest AI returned no voices.");
        // Both pools are public catalogs, so a pool without voices is a provider fault, not a change. Saving it would
        // drop every voice of that pool, and spoken feedback with a voice saved there would fail. The button still saves it.
        if (_voices.Select(v => v.Model).Distinct().Any(pool => !snapshot.Any(v => v.Model == pool)))
            throw new InvalidDataException("Smallest AI listed no voices for a voice pool.");
        // Saves nothing once the plugin was deactivated or the key changed since the fetch.
        TryApplyVoices(host, key, snapshot, ct);
    }

    /// <summary>Loads both pools. A pool that fails fails the whole fetch, so no partial catalog is saved.</summary>
    private async Task<SpeechVoice[]> FetchVoicesAsync(string key, CancellationToken ct)
    {
        var voices = new List<SpeechVoice>();
        foreach (var model in new[] { StandardModel, "lightning_v3.1_pro" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/waves/v1/{model.Replace('_', '-')}/get_voices");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
            voices.AddRange(ParseVoices(await response.Content.ReadAsStringAsync(ct), model));
        }
        return voices.DistinctBy(VoiceKey).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Saves the voices with their fetch date, unless the plugin was deactivated, the key changed or the refresh was
    /// cancelled since the fetch. The saved voice is not touched: it applies whenever the catalogs offer it.
    /// </summary>
    private bool TryApplyVoices(IPluginHostServices host, string key, SpeechVoice[] voices, CancellationToken refreshToken)
    {
        var replaced = false;
        try
        {
            lock (_voiceLock)
            {
                if (refreshToken.IsCancellationRequested || !ReferenceEquals(host, _host) || key != _apiKey) return false;
                host.SetSetting(VoicesSetting, voices);
                _voices = voices;
                replaced = true;
                var fetchedAt = _voiceRefresh.UtcNow;
                host.SetSetting(VoicesFetchedAtSetting, fetchedAt);
                _voicesFetchedAt = fetchedAt;
            }
            return true;
        }
        finally
        {
            if (replaced) host.NotifyCapabilitiesChanged();
        }
    }

    // The failure kind and status only: a provider's error message can repeat the response body.
    private static string Describe(Exception ex) => ex is PluginRequestException request
        ? request.FailureKind + (request.HttpStatusCode is { } status ? $", HTTP {status}" : "")
        : ex.GetType().Name;

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

    internal static void ValidateSpeechAudio(byte[] audio)
    {
        if (audio.Length > MaximumSpeechAudioBytes)
            throw new InvalidDataException("Speech audio exceeds the 12 MiB playback limit.");
        if (audio.Length < 44 || !audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !audio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Smallest AI returned no playable WAV audio.");
        using var stream = new MemoryStream(audio, writable: false);
        using var wave = new NAudio.Wave.WaveFileReader(stream);
        var seconds = wave.TotalTime.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > MaximumSpeechAudioSeconds)
            throw new InvalidDataException("Speech audio must be no longer than two minutes.");
    }

    internal Func<byte[], string?, ITtsPlaybackSession> PlaybackFactory { get; set; } = (audio, device) =>
        OperatingSystem.IsWindows() ? new SmallestAiPlaybackSession(audio, device) : throw new PlatformNotSupportedException("Speech playback requires Windows.");

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
        if (response.Content.Headers.ContentLength > MaximumSpeechAudioBytes)
            throw new InvalidDataException("Speech audio exceeds the 12 MiB playback limit.");
        var audio = await response.Content.ReadAsByteArrayAsync(ct);
        ValidateSpeechAudio(audio);
        ct.ThrowIfCancellationRequested();
        return PlaybackFactory(audio, request.OutputDeviceId);
    }
}
