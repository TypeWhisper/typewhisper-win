using System.IO;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SupertonicTts;

public sealed class SupertonicTtsPlugin : ITtsProviderPlugin
{
    internal const string LicenseAcceptedSettingName = "licenseAccepted";
    internal const string SelectedVoiceSettingName = "selectedVoice";
    internal const string SpeedSettingName = "speed";
    internal const string DenoisingStepsSettingName = "denoisingSteps";
    internal const string DefaultVoiceId = "M1";
    internal const double DefaultSpeed = 1.05;
    internal const int DefaultDenoisingSteps = 8;
    internal const double MinSpeed = 0.9;
    internal const double MaxSpeed = 1.5;
    internal const int MinDenoisingSteps = 1;
    internal const int MaxDenoisingSteps = 16;

    private static readonly IReadOnlyList<PluginVoiceInfo> Voices =
    [
        new("M1", "M1"),
        new("M2", "M2"),
        new("M3", "M3"),
        new("M4", "M4"),
        new("M5", "M5"),
        new("F1", "F1"),
        new("F2", "F2"),
        new("F3", "F3"),
        new("F4", "F4"),
        new("F5", "F5"),
    ];

    private readonly ISupertonicAssetManager? _injectedAssetManager;
    private readonly Func<string, ISupertonicSynthesizer> _synthesizerFactory;
    private readonly Func<float[], int, ITtsPlaybackSession> _playbackFactory;
    private readonly SemaphoreSlim _synthesisLock = new(1, 1);
    private ISupertonicAssetManager? _assetManager;
    private ISupertonicSynthesizer? _synthesizer;
    private IPluginHostServices? _host;
    private string _selectedVoiceId = DefaultVoiceId;
    private bool _licenseAccepted;
    private bool _disposed;

    public SupertonicTtsPlugin()
        : this(
            assetManager: null,
            synthesizerFactory: assetRoot => new SupertonicOnnxSynthesizer(assetRoot),
            playbackFactory: (samples, sampleRate) => new SupertonicTtsPlaybackSession(samples, sampleRate),
            useNullableAssetManagerOverload: true)
    {
    }

    internal SupertonicTtsPlugin(
        ISupertonicAssetManager assetManager,
        Func<string, ISupertonicSynthesizer> synthesizerFactory,
        Func<float[], int, ITtsPlaybackSession>? playbackFactory = null)
        : this(assetManager, synthesizerFactory, playbackFactory, useNullableAssetManagerOverload: true)
    {
    }

    private SupertonicTtsPlugin(
        ISupertonicAssetManager? assetManager,
        Func<string, ISupertonicSynthesizer> synthesizerFactory,
        Func<float[], int, ITtsPlaybackSession>? playbackFactory,
        bool useNullableAssetManagerOverload)
    {
        _injectedAssetManager = assetManager;
        _assetManager = assetManager;
        _synthesizerFactory = synthesizerFactory;
        _playbackFactory = playbackFactory ?? ((samples, sampleRate) => new SupertonicTtsPlaybackSession(samples, sampleRate));
    }

    public string PluginId => "com.typewhisper.supertonic-tts";
    public string PluginName => "Supertonic TTS";
    public string PluginVersion => "1.0.0";
    public string ProviderId => "supertonic-tts";
    public string ProviderDisplayName => "Supertonic TTS";
    public bool IsConfigured => _assetManager?.AreAssetsReady ?? false;
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => Voices;
    public string? SelectedVoiceId => _selectedVoiceId;
    internal double Speed { get; private set; } = DefaultSpeed;
    internal int DenoisingSteps { get; private set; } = DefaultDenoisingSteps;
    internal bool HasAcceptedModelLicense => _licenseAccepted;
    internal bool AreAssetsReady => IsConfigured;
    internal IPluginLocalization? Loc => _host?.Localization;

    public string? SettingsSummary
    {
        get
        {
            var status = IsConfigured ? "ready" : "download required";
            return $"Voice: {_selectedVoiceId}; speed {Speed:0.##}; steps {DenoisingSteps}; {status}";
        }
    }

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _assetManager = _injectedAssetManager
            ?? new SupertonicAssetManager(Path.Combine(host.PluginAssetDirectory, "Models", SupertonicPaths.ModelDirectoryName));
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        Speed = NormalizeSpeed(host.GetSetting<double?>(SpeedSettingName) ?? DefaultSpeed);
        DenoisingSteps = NormalizeDenoisingSteps(host.GetSetting<int?>(DenoisingStepsSettingName) ?? DefaultDenoisingSteps);
        _licenseAccepted = host.GetSetting<bool?>(LicenseAcceptedSettingName).GetValueOrDefault();
        PersistSettings();
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        _synthesizer?.Dispose();
        _synthesizer = null;
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new SupertonicSettingsView(this);

    public void SelectVoice(string? voiceId)
    {
        _selectedVoiceId = NormalizeVoiceId(voiceId);
        _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
    }

    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return SupertonicInactiveTtsPlaybackSession.Instance;

        if (_assetManager?.AreAssetsReady != true)
            throw new InvalidOperationException("Supertonic 3 assets are not downloaded. Open plugin settings to download them.");

        await _synthesisLock.WaitAsync(ct);
        try
        {
            var synthesizer = _synthesizer ??= _synthesizerFactory(_assetManager.AssetRoot);
            var synthesis = synthesizer.Synthesize(
                new SupertonicSynthesisRequest(
                    text,
                    NormalizeLanguage(request.Language),
                    SupertonicPaths.VoiceStylePath(_assetManager.AssetRoot, _selectedVoiceId),
                    DenoisingSteps,
                    Speed),
                ct);

            return synthesis.Samples.Length == 0
                ? SupertonicInactiveTtsPlaybackSession.Instance
                : _playbackFactory(synthesis.Samples, synthesis.SampleRate);
        }
        finally
        {
            _synthesisLock.Release();
        }
    }

    internal void SetLicenseAccepted(bool accepted)
    {
        _licenseAccepted = accepted;
        _host?.SetSetting(LicenseAcceptedSettingName, accepted);
    }

    internal void SetSpeed(double speed)
    {
        Speed = NormalizeSpeed(speed);
        _host?.SetSetting(SpeedSettingName, Speed);
    }

    internal void SetDenoisingSteps(int steps)
    {
        DenoisingSteps = NormalizeDenoisingSteps(steps);
        _host?.SetSetting(DenoisingStepsSettingName, DenoisingSteps);
    }

    internal async Task DownloadAssetsAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!_licenseAccepted)
            throw new InvalidOperationException("The Supertonic 3 OpenRAIL-M license must be accepted before downloading model assets.");

        if (_assetManager is null)
            throw new InvalidOperationException("Plugin is not activated.");

        await _assetManager.DownloadMissingAssetsAsync(progress, ct);
        _synthesizer?.Dispose();
        _synthesizer = null;
        _host?.NotifyCapabilitiesChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _synthesizer?.Dispose();
        if (_injectedAssetManager is null && _assetManager is IDisposable disposableAssets)
            disposableAssets.Dispose();
        _synthesisLock.Dispose();
    }

    internal static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "en";

        var normalized = language.Trim().ToLowerInvariant();
        var separator = normalized.IndexOfAny(['-', '_']);
        if (separator > 0)
            normalized = normalized[..separator];

        return SupertonicTextProcessor.SupportedLanguages.Contains(normalized)
            ? normalized
            : "en";
    }

    internal static double NormalizeSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed))
            return DefaultSpeed;
        return Math.Round(Math.Max(MinSpeed, Math.Min(MaxSpeed, speed)), 2);
    }

    internal static int NormalizeDenoisingSteps(int steps) =>
        Math.Max(MinDenoisingSteps, Math.Min(MaxDenoisingSteps, steps));

    private static string NormalizeVoiceId(string? voiceId) =>
        !string.IsNullOrWhiteSpace(voiceId)
        && Voices.Any(voice => string.Equals(voice.Id, voiceId.Trim(), StringComparison.OrdinalIgnoreCase))
            ? Voices.First(voice => string.Equals(voice.Id, voiceId.Trim(), StringComparison.OrdinalIgnoreCase)).Id
            : DefaultVoiceId;

    private void PersistSettings()
    {
        if (_host is null)
            return;

        _host.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
        _host.SetSetting(SpeedSettingName, Speed);
        _host.SetSetting(DenoisingStepsSettingName, DenoisingSteps);
    }
}
