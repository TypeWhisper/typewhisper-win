using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.Services;

public sealed record TtsProviderOption(string Id, string DisplayName);
public sealed record TtsVoiceOption(string Id, string DisplayName, string? LocaleIdentifier = null);

/// <summary>
/// Provides spoken text-to-speech feedback for transcription events.
/// </summary>
public sealed class SpeechFeedbackService : IDisposable
{
    public const string DefaultVoiceOptionId = "__typewhisper_default_voice__";

    private readonly ISettingsService _settings;
    private readonly PluginManager _pluginManager;
    private readonly ITtsProviderPlugin _systemProvider;
    private readonly object _lock = new();

    private CancellationTokenSource? _speakCts;
    private ITtsPlaybackSession? _playbackSession;
    private bool _isPlaybackPending;
    private bool _disposed;
    private long _playbackVersion;

    public SpeechFeedbackService(ISettingsService settings, PluginManager pluginManager)
        : this(settings, pluginManager, new WindowsSapiTtsProvider(settings))
    {
    }

    internal SpeechFeedbackService(
        ISettingsService settings,
        PluginManager pluginManager,
        ITtsProviderPlugin systemProvider)
    {
        _settings = settings;
        _pluginManager = pluginManager;
        _systemProvider = systemProvider;
        _pluginManager.PluginStateChanged += OnPluginStateChanged;
    }

    public bool IsEnabled { get; set; }

    public bool IsSpeaking
    {
        get
        {
            lock (_lock)
                return _isPlaybackPending || _playbackSession?.IsActive == true;
        }
    }

    public event EventHandler? ProvidersChanged;

    public IReadOnlyList<TtsProviderOption> AvailableProviders =>
        AllProviders()
            .Select(p => new TtsProviderOption(p.ProviderId, p.ProviderDisplayName))
            .ToList();

    public string EffectiveProviderId => ResolveSpeakProvider().ProviderId;

    public IReadOnlyList<TtsVoiceOption> GetVoiceOptions(string? providerId)
    {
        var provider = FindProvider(providerId) ?? _systemProvider;
        var voices = new List<TtsVoiceOption>
        {
            new(DefaultVoiceOptionId, Loc.Instance["Tts.SystemDefaultVoice"])
        };

        voices.AddRange(provider.AvailableVoices.Select(v =>
        {
            var displayName = string.IsNullOrWhiteSpace(v.LocaleIdentifier)
                ? v.DisplayName
                : $"{v.DisplayName} ({v.LocaleIdentifier})";
            return new TtsVoiceOption(v.Id, displayName, v.LocaleIdentifier);
        }));

        return voices;
    }

    public string? GetSelectedVoiceId(string? providerId)
    {
        var provider = FindProvider(providerId) ?? _systemProvider;
        return string.IsNullOrWhiteSpace(provider.SelectedVoiceId)
            ? DefaultVoiceOptionId
            : provider.SelectedVoiceId;
    }

    public void SelectVoice(string? providerId, string? voiceId)
    {
        var provider = FindProvider(providerId) ?? _systemProvider;
        try
        {
            provider.SelectVoice(IsDefaultVoiceOptionId(voiceId) ? null : voiceId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpeechFeedback voice selection error: {ex.Message}");
        }
    }

    public static bool IsDefaultVoiceOptionId(string? voiceId) =>
        string.IsNullOrWhiteSpace(voiceId) ||
        string.Equals(voiceId, DefaultVoiceOptionId, StringComparison.Ordinal);

    public void Speak(string text, string? language = null) =>
        SpeakCore(new TtsSpeakRequest(text, language, TtsPurpose.Status), requireEnabled: true);

    public void SpeakAutomaticTranscription(string text, string? language = null) =>
        SpeakCore(new TtsSpeakRequest(text, language, TtsPurpose.Transcription), requireEnabled: true);

    public void ReadBack(string text, string? language = null)
    {
        if (IsSpeaking)
        {
            Stop();
            return;
        }

        SpeakCore(new TtsSpeakRequest(text, language, TtsPurpose.ManualReadback), requireEnabled: false);
    }

    public void AnnounceRecordingStarted() => Speak("Recording");

    public void AnnounceTranscriptionComplete(string text, string? language = null) =>
        SpeakAutomaticTranscription(text, language);

    public void AnnounceError(string reason) => Speak($"Error: {reason}");

    public void Stop()
    {
        CancellationTokenSource? cts;
        ITtsPlaybackSession? session;

        lock (_lock)
        {
            cts = _speakCts;
            session = _playbackSession;
            _speakCts = null;
            _playbackSession = null;
            _isPlaybackPending = false;
        }

        try { cts?.Cancel(); }
        catch { }
        finally { cts?.Dispose(); }

        try { session?.Stop(); }
        catch { }
    }

    private void SpeakCore(TtsSpeakRequest request, bool requireEnabled)
    {
        if (_disposed || string.IsNullOrWhiteSpace(request.Text)) return;
        if (requireEnabled && !IsEnabled) return;

        Stop();

        var cts = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _playbackVersion);

        lock (_lock)
        {
            _speakCts = cts;
            _isPlaybackPending = true;
        }

        _ = SpeakAsync(request, cts, version);
    }

    private async Task SpeakAsync(TtsSpeakRequest request, CancellationTokenSource cts, long version)
    {
        ITtsPlaybackSession? session = null;
        try
        {
            var provider = ResolveSpeakProvider();
            session = await provider.SpeakAsync(request, cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested)
            {
                session.Stop();
                return;
            }

            var accepted = false;
            lock (_lock)
            {
                if (_speakCts == cts && version == Volatile.Read(ref _playbackVersion))
                {
                    _playbackSession = session;
                    _isPlaybackPending = false;
                    accepted = true;
                }
            }

            if (!accepted)
            {
                session.Stop();
                return;
            }

            EventHandler? completedHandler = null;
            completedHandler = (_, _) =>
            {
                session.Completed -= completedHandler;
                OnPlaybackCompleted(session, cts, version);
            };
            session.Completed += completedHandler;

            if (!session.IsActive)
                OnPlaybackCompleted(session, cts, version);
        }
        catch (OperationCanceledException)
        {
            ClearPending(cts, version);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpeechFeedback error: {ex.Message}");
            ClearPending(cts, version);
        }
    }

    private void OnPlaybackCompleted(ITtsPlaybackSession session, CancellationTokenSource cts, long version)
    {
        var disposeCts = false;
        lock (_lock)
        {
            if (ReferenceEquals(_playbackSession, session) && version == Volatile.Read(ref _playbackVersion))
            {
                _playbackSession = null;
                _isPlaybackPending = false;
                if (_speakCts == cts)
                {
                    _speakCts = null;
                    disposeCts = true;
                }
            }
        }

        if (disposeCts)
            cts.Dispose();
    }

    private void ClearPending(CancellationTokenSource cts, long version)
    {
        var disposeCts = false;
        lock (_lock)
        {
            if (_speakCts == cts && version == Volatile.Read(ref _playbackVersion))
            {
                _speakCts = null;
                _isPlaybackPending = false;
                disposeCts = true;
            }
        }

        if (disposeCts)
            cts.Dispose();
    }

    private IReadOnlyList<ITtsProviderPlugin> AllProviders() =>
        [_systemProvider, .. _pluginManager.TtsProviders];

    private ITtsProviderPlugin? FindProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) ||
            string.Equals(providerId, _systemProvider.ProviderId, StringComparison.OrdinalIgnoreCase))
            return _systemProvider;

        return _pluginManager.GetTtsProvider(providerId);
    }

    private ITtsProviderPlugin ResolveSpeakProvider()
    {
        var selectedProviderId = _settings.Current.SpokenFeedbackProviderId;
        var selectedProvider = FindProvider(selectedProviderId);

        if (selectedProvider is not null &&
            !ReferenceEquals(selectedProvider, _systemProvider) &&
            selectedProvider.IsConfigured)
        {
            return selectedProvider;
        }

        return _systemProvider;
    }

    private void OnPluginStateChanged(object? sender, EventArgs e) =>
        ProvidersChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pluginManager.PluginStateChanged -= OnPluginStateChanged;
        Stop();
        _systemProvider.Dispose();
    }
}
