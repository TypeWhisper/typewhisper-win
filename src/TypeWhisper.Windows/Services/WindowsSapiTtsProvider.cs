using System.Diagnostics;
using System.Speech.Synthesis;
using System.Windows.Controls;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Built-in Windows SAPI text-to-speech provider used as the default and fallback.
/// </summary>
public sealed class WindowsSapiTtsProvider : ITtsProviderPlugin
{
    /// <summary>
    /// Defines the built in provider id constant.
    /// </summary>
    public const string BuiltInProviderId = AppSettings.DefaultSpokenFeedbackProviderId;

    private readonly ISettingsService _settings;

    /// <summary>
    /// Initializes a new instance of the WindowsSapiTtsProvider class.
    /// </summary>
    public WindowsSapiTtsProvider(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.tts.windows-sapi";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Windows System Voice";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";
    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => BuiltInProviderId;
    /// <summary>
    /// Gets the provider name displayed in the UI.
    /// </summary>
    public string ProviderDisplayName => Loc.Instance["Tts.WindowsSapiProvider"];
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => true;
    /// <summary>
    /// Gets the currently selected provider voice identifier.
    /// </summary>
    public string? SelectedVoiceId => _settings.Current.SpokenFeedbackVoiceId;

    /// <summary>
    /// Gets the voices exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices
    {
        get
        {
            try
            {
                using var synth = new SpeechSynthesizer();
                return synth.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .Select(v => new PluginVoiceInfo(
                        v.VoiceInfo.Name,
                        v.VoiceInfo.Name,
                        v.VoiceInfo.Culture?.Name))
                    .OrderBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsSapiTtsProvider] Failed to enumerate voices: {ex.Message}");
                return [];
            }
        }
    }

    /// <summary>
    /// Gets the user-facing summary of the current settings.
    /// </summary>
    public string? SettingsSummary
    {
        get
        {
            var voice = AvailableVoices.FirstOrDefault(v => v.Id == SelectedVoiceId);
            return voice is null
                ? Loc.Instance["Tts.SystemDefaultVoice"]
                : voice.DisplayName;
        }
    }

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => null;

    /// <summary>
    /// Selects the provider voice used for subsequent speech output.
    /// </summary>
    public void SelectVoice(string? voiceId)
    {
        var normalized = string.IsNullOrWhiteSpace(voiceId) ? null : voiceId;
        if (normalized is not null && AvailableVoices.All(v => v.Id != normalized))
            normalized = null;

        if (_settings.Current.SpokenFeedbackVoiceId == normalized)
            return;

        _settings.Save(_settings.Current with { SpokenFeedbackVoiceId = normalized });
    }

    /// <summary>
    /// Synthesizes speech and returns a playback session.
    /// </summary>
    public Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return Task.FromResult<ITtsPlaybackSession>(InactiveTtsPlaybackSession.Instance);

        ct.ThrowIfCancellationRequested();

        var synth = new SpeechSynthesizer();
        try
        {
            synth.SetOutputToDefaultAudioDevice();
            if (!string.IsNullOrWhiteSpace(SelectedVoiceId))
            {
                try
                {
                    synth.SelectVoice(SelectedVoiceId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WindowsSapiTtsProvider] Failed to select voice '{SelectedVoiceId}': {ex.Message}");
                }
            }

            var session = new SapiTtsPlaybackSession(synth, ct);
            session.Start(request.Text);
            return Task.FromResult<ITtsPlaybackSession>(session);
        }
        catch
        {
            synth.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
    }
}

internal sealed class SapiTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly SpeechSynthesizer _synth;
    private int _completed;

    /// <summary>
    /// Performs sapi tts playback session.
    /// </summary>
    public SapiTtsPlaybackSession(SpeechSynthesizer synth, CancellationToken ct)
    {
        _synth = synth;
        _synth.SpeakCompleted += OnSpeakCompleted;
        ct.Register(Stop);
    }

    /// <summary>
    /// Gets whether this item is currently active.
    /// </summary>
    public bool IsActive => Volatile.Read(ref _completed) == 0;

    /// <summary>
    /// Raised when playback or the asynchronous operation completes.
    /// </summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Starts the service or session.
    /// </summary>
    public void Start(string text)
    {
        _synth.SpeakAsync(text);
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
    public void Stop()
    {
        if (!IsActive) return;

        try
        {
            _synth.SpeakAsyncCancelAll();
        }
        catch { }

        Finish();
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e) => Finish();

    private void Finish()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _synth.SpeakCompleted -= OnSpeakCompleted;
        _synth.Dispose();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => Stop();
}

internal sealed class InactiveTtsPlaybackSession : ITtsPlaybackSession
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static InactiveTtsPlaybackSession Instance { get; } = new();

    private InactiveTtsPlaybackSession()
    {
    }

    /// <summary>
    /// Gets whether this item is currently active.
    /// </summary>
    public bool IsActive => false;

    /// <summary>
    /// Raised when playback or the asynchronous operation completes.
    /// </summary>
    public event EventHandler? Completed
    {
        add { value?.Invoke(this, EventArgs.Empty); }
        remove { }
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
    public void Stop()
    {
    }
}
