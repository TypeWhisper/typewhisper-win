using TypeWhisper.PluginSDK.Models;
namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>Speech fixture with an explicitly drained playback stop.</summary>
public sealed class TtsProbePlugin : ITtsProviderPlugin
{
    private IPluginHostServices _host = null!;
    /// <inheritdoc />
    public string PluginId => "test.typewhisper.runtime";
    /// <inheritdoc />
    public string PluginName => "Speech fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => PluginId;
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public bool IsConfigured => true;
    /// <inheritdoc />
    public bool SupportsPlaybackSelection => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => [new("voice", "Fixture voice")];
    /// <inheritdoc />
    public string? SelectedVoiceId => "voice";
    /// <inheritdoc />
    public void SelectVoice(string? voiceId) { }
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host) { _host = host; return Task.CompletedTask; }
    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public void Dispose() => _host.SetSetting("disposals", _host.GetSetting<int>("disposals") + 1);
    /// <inheritdoc />
    public Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        _host.SetSetting("voice", request.VoiceId); _host.SetSetting("output", request.OutputDeviceId);
        _host.Log(PluginLogLevel.Info, "request-start");
        return Task.FromResult<ITtsPlaybackSession>(new Playback(_host));
    }
    private sealed class Playback(IPluginHostServices host) : ITtsPlaybackSession
    {
        private bool _stopped;
        public bool IsActive => !_stopped && !host.GetSetting<bool>("CompletedSpeech");
        public string? Error => host.GetSetting<bool>("FailedSpeech") ? "Fixture playback failed." : null;
        public event EventHandler? Completed;
        public void Stop()
        {
            if (_stopped) return;
            if (host.GetSetting<bool>("Hold")) host.LoadSecretAsync("hold").GetAwaiter().GetResult();
            _stopped = true; host.SetSetting("stops", host.GetSetting<int>("stops") + 1);
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }
}
