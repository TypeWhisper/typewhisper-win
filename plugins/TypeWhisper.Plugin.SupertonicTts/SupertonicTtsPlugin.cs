using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SupertonicTts;

/// <summary>
/// Provides supertonic tts plugin behavior.
/// </summary>
public sealed class SupertonicTtsPlugin : ITtsProviderPlugin, IModelDownloadRequirementsProvider
{
    internal const string LicenseAcceptedSettingName = "licenseAccepted";
    internal const string AcceptedModelLicenseIdSettingName = "acceptedModelLicenseId";
    internal const string AcceptedModelLicenseRevisionSettingName = "acceptedModelLicenseRevision";
    internal const string AcceptedModelLicenseAtSettingName = "acceptedModelLicenseAt";
    internal const string ModelId = "supertonic-3";
    internal const string ModelLicenseRequirementId = "model-license";
    internal const string HuggingFaceTokenRequirementId = "hugging-face-token";
    internal const string ModelLicenseId = "Supertone/supertonic-3";
    internal const string ModelLicenseRevision = "openrail-m-2022-08-18";
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
    private readonly HttpClient? _huggingFaceTokenValidationClient;
    private readonly SemaphoreSlim _synthesisLock = new(1, 1);
    private ISupertonicAssetManager? _assetManager;
    private ISupertonicSynthesizer? _synthesizer;
    private IPluginHostServices? _host;
    private string _selectedVoiceId = DefaultVoiceId;
    private string? _acceptedModelLicenseId;
    private string? _acceptedModelLicenseRevision;
    private string? _huggingFaceToken;
    private bool _disposed;

    /// <summary>Raised when model license or optional authentication state changes.</summary>
    public event EventHandler? ModelDownloadRequirementsChanged;

    /// <summary>
    /// Initializes a new instance of the SupertonicTtsPlugin class.
    /// </summary>
    public SupertonicTtsPlugin()
        : this(
            assetManager: null,
            synthesizerFactory: assetRoot => new SupertonicOnnxSynthesizer(assetRoot),
            playbackFactory: (samples, sampleRate) => new SupertonicTtsPlaybackSession(samples, sampleRate),
            huggingFaceTokenValidationClient: null,
            useNullableAssetManagerOverload: true)
    {
    }

    internal SupertonicTtsPlugin(
        ISupertonicAssetManager assetManager,
        Func<string, ISupertonicSynthesizer> synthesizerFactory,
        Func<float[], int, ITtsPlaybackSession>? playbackFactory = null,
        HttpClient? huggingFaceTokenValidationClient = null)
        : this(
            assetManager,
            synthesizerFactory,
            playbackFactory,
            huggingFaceTokenValidationClient,
            useNullableAssetManagerOverload: true)
    {
    }

    private SupertonicTtsPlugin(
        ISupertonicAssetManager? assetManager,
        Func<string, ISupertonicSynthesizer> synthesizerFactory,
        Func<float[], int, ITtsPlaybackSession>? playbackFactory,
        HttpClient? huggingFaceTokenValidationClient,
        bool useNullableAssetManagerOverload)
    {
        _injectedAssetManager = assetManager;
        _assetManager = assetManager;
        _synthesizerFactory = synthesizerFactory;
        _playbackFactory = playbackFactory ?? ((samples, sampleRate) => new SupertonicTtsPlaybackSession(samples, sampleRate));
        _huggingFaceTokenValidationClient = huggingFaceTokenValidationClient;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.supertonic-tts";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Supertonic TTS";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";
    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "supertonic-tts";
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => "Supertonic TTS";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => _assetManager?.AreAssetsReady ?? false;
    /// <summary>
    /// Gets the voices exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => Voices;
    /// <summary>
    /// Gets the currently selected provider voice identifier.
    /// </summary>
    public string? SelectedVoiceId => _selectedVoiceId;
    internal double Speed { get; private set; } = DefaultSpeed;
    internal int DenoisingSteps { get; private set; } = DefaultDenoisingSteps;
    internal bool HasAcceptedModelLicense =>
        string.Equals(_acceptedModelLicenseId, ModelLicenseId, StringComparison.Ordinal)
        && string.Equals(_acceptedModelLicenseRevision, ModelLicenseRevision, StringComparison.Ordinal);
    internal bool AreAssetsReady => IsConfigured;
    internal IPluginLocalization? Loc => _host?.Localization;

    /// <summary>Gets the host-renderable model license and optional token requirements.</summary>
    public IReadOnlyList<PluginModelDownloadRequirement> ModelDownloadRequirements =>
    [
        new PluginModelDownloadRequirement(
            ModelId,
            "Supertonic 3",
            ModelLicenseRequirementId,
            PluginModelDownloadRequirementKind.License,
            Loc?.GetString("Settings.LicenseTitle") ?? "Model license",
            Loc?.GetString("Settings.LicenseDescription")
                ?? "Review and accept the OpenRAIL-M model license before downloading.",
            IsRequired: true,
            IsSatisfied: HasAcceptedModelLicense)
        {
            MoreInfoUri = new Uri("https://huggingface.co/Supertone/supertonic-3/blob/main/LICENSE"),
            Revision = ModelLicenseRevision
        },
        new PluginModelDownloadRequirement(
            ModelId,
            "Supertonic 3",
            HuggingFaceTokenRequirementId,
            PluginModelDownloadRequirementKind.Credential,
            Loc?.GetString("Settings.HuggingFaceToken") ?? "Hugging Face token (optional)",
            Loc?.GetString("Settings.HuggingFaceTokenHint")
                ?? "Optional authentication for Hugging Face model downloads.",
            IsRequired: false,
            IsSatisfied: _huggingFaceToken is not null)
        {
            MoreInfoUri = new Uri("https://huggingface.co/settings/tokens")
        }
    ];

    /// <summary>
    /// Gets the user-facing summary of the current settings.
    /// </summary>
    public string? SettingsSummary
    {
        get
        {
            var status = IsConfigured ? "ready" : "download required";
            return $"Voice: {_selectedVoiceId}; speed {Speed:0.##}; steps {DenoisingSteps}; {status}";
        }
    }

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        var modelDirectoryName = Path.GetFileName(SupertonicPaths.ModelDirectoryName);
        if (string.IsNullOrWhiteSpace(modelDirectoryName) || modelDirectoryName is "." or "..")
            throw new InvalidOperationException("Supertonic model directory name must not be empty.");

        _assetManager = _injectedAssetManager
            ?? new SupertonicAssetManager(Path.Join(host.PluginAssetDirectory, "Models", modelDirectoryName));
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        Speed = NormalizeSpeed(host.GetSetting<double?>(SpeedSettingName) ?? DefaultSpeed);
        DenoisingSteps = NormalizeDenoisingSteps(host.GetSetting<int?>(DenoisingStepsSettingName) ?? DefaultDenoisingSteps);
        _acceptedModelLicenseId = host.GetSetting<string>(AcceptedModelLicenseIdSettingName);
        _acceptedModelLicenseRevision = host.GetSetting<string>(AcceptedModelLicenseRevisionSettingName);
        if (!HasAcceptedModelLicense
            && host.GetSetting<bool?>(LicenseAcceptedSettingName).GetValueOrDefault())
        {
            _acceptedModelLicenseId = ModelLicenseId;
            _acceptedModelLicenseRevision = ModelLicenseRevision;
            host.SetSetting(AcceptedModelLicenseIdSettingName, _acceptedModelLicenseId);
            host.SetSetting(AcceptedModelLicenseRevisionSettingName, _acceptedModelLicenseRevision);
            host.SetSetting(AcceptedModelLicenseAtSettingName, DateTimeOffset.UtcNow.ToString("O"));
        }

        try
        {
            _huggingFaceToken = await PluginHuggingFaceTokenHelper.LoadTokenAsync(host);
        }
        catch (ArgumentException)
        {
            _huggingFaceToken = null;
            await PluginHuggingFaceTokenHelper.ClearTokenAsync(host);
        }
        PersistSettings();
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _synthesizer?.Dispose();
        _synthesizer = null;
        _host = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new SupertonicSettingsView(this);

    /// <summary>
    /// Selects the provider voice used for subsequent speech output.
    /// </summary>
    public void SelectVoice(string? voiceId)
    {
        _selectedVoiceId = NormalizeVoiceId(voiceId);
        _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
    }

    /// <summary>
    /// Synthesizes speech and returns a playback session.
    /// </summary>
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
        _acceptedModelLicenseId = accepted ? ModelLicenseId : null;
        _acceptedModelLicenseRevision = accepted ? ModelLicenseRevision : null;
        _host?.SetSetting(LicenseAcceptedSettingName, accepted);
        _host?.SetSetting(AcceptedModelLicenseIdSettingName, _acceptedModelLicenseId);
        _host?.SetSetting(AcceptedModelLicenseRevisionSettingName, _acceptedModelLicenseRevision);
        _host?.SetSetting(
            AcceptedModelLicenseAtSettingName,
            accepted ? DateTimeOffset.UtcNow.ToString("O") : null);
        _host?.NotifyCapabilitiesChanged();
        ModelDownloadRequirementsChanged?.Invoke(this, EventArgs.Empty);
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
        if (!HasAcceptedModelLicense)
            throw new InvalidOperationException("The Supertonic 3 OpenRAIL-M license must be accepted before downloading model assets.");

        if (_assetManager is null)
            throw new InvalidOperationException("Plugin is not activated.");

        await _assetManager.DownloadMissingAssetsAsync(progress, _huggingFaceToken, ct);
        _synthesizer?.Dispose();
        _synthesizer = null;
        _host?.NotifyCapabilitiesChanged();
    }

    /// <summary>Validates and stores the optional Hugging Face token.</summary>
    public async Task<PluginModelDownloadRequirementResult> SaveModelDownloadCredentialAsync(
        string modelId,
        string requirementId,
        string credential,
        CancellationToken ct)
    {
        ValidateRequirement(modelId, requirementId, HuggingFaceTokenRequirementId);
        var isValid = await PluginHuggingFaceTokenHelper.ValidateTokenAsync(
            credential,
            _huggingFaceTokenValidationClient,
            ct);
        if (!isValid)
        {
            return new PluginModelDownloadRequirementResult(
                false,
                Loc?.GetString("Settings.InvalidToken") ?? "The Hugging Face token is invalid.");
        }

        if (_host is null)
            return new PluginModelDownloadRequirementResult(false, "Plugin is not activated.");

        _huggingFaceToken = await PluginHuggingFaceTokenHelper.SaveTokenAsync(_host, credential);
        _host.NotifyCapabilitiesChanged();
        ModelDownloadRequirementsChanged?.Invoke(this, EventArgs.Empty);
        return new PluginModelDownloadRequirementResult(
            true,
            Loc?.GetString("Settings.TokenSaved") ?? "Token saved securely.");
    }

    /// <summary>Clears the optional Hugging Face token.</summary>
    public async Task ClearModelDownloadCredentialAsync(
        string modelId,
        string requirementId,
        CancellationToken ct)
    {
        ValidateRequirement(modelId, requirementId, HuggingFaceTokenRequirementId);
        ct.ThrowIfCancellationRequested();
        if (_host is not null)
            await PluginHuggingFaceTokenHelper.ClearTokenAsync(_host);
        _huggingFaceToken = null;
        _host?.NotifyCapabilitiesChanged();
        ModelDownloadRequirementsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Persists acceptance for the current model license revision.</summary>
    public Task SetModelDownloadLicenseAcceptanceAsync(
        string modelId,
        string requirementId,
        bool accepted,
        CancellationToken ct)
    {
        ValidateRequirement(modelId, requirementId, ModelLicenseRequirementId);
        ct.ThrowIfCancellationRequested();
        SetLicenseAccepted(accepted);
        return Task.CompletedTask;
    }

    private static void ValidateRequirement(string modelId, string requirementId, string expectedRequirementId)
    {
        if (!string.Equals(modelId, ModelId, StringComparison.Ordinal)
            || !string.Equals(requirementId, expectedRequirementId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unknown model download requirement.");
        }
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
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
