using TypeWhisper.PluginSDK.Models;
namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>Diarization fixture whose behaviour the test sets through plugin settings.</summary>
public sealed class DiarizationProbePlugin : IAudioDiarizationProvider
{
    private IPluginHostServices _host = null!;
    /// <inheritdoc />
    public string PluginId => "test.typewhisper.runtime";
    /// <inheritdoc />
    public string PluginName => "Diarization fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string DiarizationProviderId => "fixture-diarization";
    /// <inheritdoc />
    public string DiarizationProviderDisplayName => PluginName;
    /// <inheritdoc />
    public bool AreDiarizationModelsInstalled => _host.GetSetting<bool>("Installed");
    /// <inheritdoc />
    public PluginModelInfo? DiarizationModel => new("fixture-model", "Fixture model") { SizeDescription = "1 MB" };
    /// <inheritdoc />
    public PluginSpeakerCountRange? SupportedSpeakerCounts => new(2, 4);
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host) { _host = host; return Task.CompletedTask; }
    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public void Dispose() => _host.SetSetting("disposals", _host.GetSetting<int>("disposals") + 1);
    /// <inheritdoc />
    public async Task PrepareDiarizationModelsAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        _host.SetSetting("prepares", _host.GetSetting<int>("prepares") + 1);
        progress?.Report(0.5);
        if (_host.GetSetting<bool>("Hold"))
        {
            _host.Log(PluginLogLevel.Info, "request-start");
            await _host.LoadSecretAsync("hold").WaitAsync(cancellationToken);
        }
        _host.SetSetting("Installed", true);
        progress?.Report(1);
    }
    /// <inheritdoc />
    public Task DeleteDiarizationModelsAsync(CancellationToken cancellationToken) { _host.SetSetting("Installed", false); return Task.CompletedTask; }
    /// <inheritdoc />
    public Task UnloadDiarizationModelsAsync() { _host.SetSetting("unloads", _host.GetSetting<int>("unloads") + 1); return Task.CompletedTask; }
    /// <inheritdoc />
    public async Task<PluginDiarizationResult> DiarizeAsync(PluginDiarizationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        _host.SetSetting("samples", request.Samples.Length);
        _host.SetSetting("speakers", request.SpeakerCount);
        _host.SetSetting("language", request.Language);
        if (_host.GetSetting<bool>("Hold"))
        {
            _host.Log(PluginLogLevel.Info, "request-start");
            await _host.LoadSecretAsync("hold").WaitAsync(cancellationToken);
        }
        progress?.Report(1);
        return new([new("S1", 0, 1) { Confidence = 0.9 }, new("S2", 1, request.DurationSeconds)], "fixture") { ModelVersion = "1" };
    }
}
