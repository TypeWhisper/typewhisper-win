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
    public const string BuiltInProviderId = AppSettings.DefaultSpokenFeedbackProviderId;

    private readonly ISettingsService _settings;

    public WindowsSapiTtsProvider(ISettingsService settings)
    {
        _settings = settings;
    }

    public string PluginId => "com.typewhisper.tts.windows-sapi";
    public string PluginName => "Windows System Voice";
    public string PluginVersion => "1.0.0";
    public string ProviderId => BuiltInProviderId;
    public string ProviderDisplayName => Loc.Instance["Tts.WindowsSapiProvider"];
    public bool IsConfigured => true;
    public string? SelectedVoiceId => _settings.Current.SpokenFeedbackVoiceId;

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

    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

    public Task DeactivateAsync() => Task.CompletedTask;

    public UserControl? CreateSettingsView() => null;

    public void SelectVoice(string? voiceId)
    {
        var normalized = string.IsNullOrWhiteSpace(voiceId) ? null : voiceId;
        if (normalized is not null && AvailableVoices.All(v => v.Id != normalized))
            normalized = null;

        if (_settings.Current.SpokenFeedbackVoiceId == normalized)
            return;

        _settings.Save(_settings.Current with { SpokenFeedbackVoiceId = normalized });
    }

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

    public void Dispose()
    {
    }
}

internal sealed class SapiTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly SpeechSynthesizer _synth;
    private int _completed;

    public SapiTtsPlaybackSession(SpeechSynthesizer synth, CancellationToken ct)
    {
        _synth = synth;
        _synth.SpeakCompleted += OnSpeakCompleted;
        ct.Register(Stop);
    }

    public bool IsActive => Volatile.Read(ref _completed) == 0;

    public event EventHandler? Completed;

    public void Start(string text)
    {
        _synth.SpeakAsync(text);
    }

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

    public void Dispose() => Stop();
}

internal sealed class InactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static InactiveTtsPlaybackSession Instance { get; } = new();

    private InactiveTtsPlaybackSession()
    {
    }

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add { value?.Invoke(this, EventArgs.Empty); }
        remove { }
    }

    public void Stop()
    {
    }
}
