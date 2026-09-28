using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>A local engine for transcription worker tests. The requested language selects a failure to simulate.</summary>
public sealed class WorkerProbePlugin : IPcmTranscriptionEnginePlugin
{
    private IPluginHostServices? _host;
    private string? _loaded;
    private TranscriptionAccelerationPreference _acceleration = TranscriptionAccelerationPreference.Auto;
    /// <inheritdoc />
    public string PluginId => "test.typewhisper.worker";
    /// <inheritdoc />
    public string PluginName => "Worker fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => "worker-fixture";
    /// <inheritdoc />
    public string ProviderDisplayName => "Worker fixture";
    /// <inheritdoc />
    public bool IsConfigured => SelectedModelId is not null;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("small", "Small"), new("large", "Large")];
    /// <inheritdoc />
    public string? SelectedModelId => _host?.GetSetting<string>("selectedModel");
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public bool SupportsModelDownload => true;
    /// <inheritdoc />
    public bool SupportsLocalLivePreview => true;
    /// <inheritdoc />
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends =>
        [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.NvidiaCuda];
    /// <inheritdoc />
    public TranscriptionAccelerationPreference AccelerationPreference => _acceleration;
    /// <inheritdoc />
    public TranscriptionAccelerationStatus AccelerationStatus => new(
        _acceleration == TranscriptionAccelerationPreference.Cpu ? TranscriptionAccelerationBackend.Cpu : TranscriptionAccelerationBackend.NvidiaCuda,
        "Fixture " + _acceleration);
    /// <inheritdoc />
    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference) => _acceleration = preference;
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _host.SetSetting("activations", _host.GetSetting<int>("activations") + 1);
        // Real engines log while activating, before the worker reports that it is ready.
        _host.Log(PluginLogLevel.Info, "activated in " + Environment.ProcessId);
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public void Dispose() { }
    /// <inheritdoc />
    public void SelectModel(string modelId) => _host!.SetSetting("selectedModel", modelId);
    /// <inheritdoc />
    public bool IsModelDownloaded(string modelId) => true;
    /// <inheritdoc />
    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        if (_host!.GetSetting<bool>("CrashOnLoad")) Environment.FailFast("Worker fixture load crash");
        _host.Log(PluginLogLevel.Info, "loaded " + modelId + " in " + Environment.ProcessId);
        _loaded = modelId;
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
        Result(language, wavAudio.Length, prompt, ct);
    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples, string? language, bool translate, CancellationToken cancellationToken) =>
        Result(language, samples.Length, samples.IsEmpty ? null : samples.Span[^1].ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    private async Task<PluginTranscriptionResult> Result(string? language, int length, string? detail, CancellationToken ct)
    {
        switch (language)
        {
            case "crash": Environment.FailFast("Worker fixture crash"); break;
            case "crash-accelerated" when _acceleration != TranscriptionAccelerationPreference.Cpu: Environment.FailFast("Worker fixture GPU crash"); break;
            case "crash-once":
                var marker = Path.Combine(_host!.PluginDataDirectory, "crashed-once");
                if (!File.Exists(marker)) { File.WriteAllText(marker, ""); Environment.FailFast("Worker fixture transient crash"); }
                break;
            case "throw": throw new NotSupportedException("Fixture engine rejected the request.");
            case "hang": await Task.Delay(Timeout.Infinite, ct); break;
            case "hang-hard": Thread.Sleep(Timeout.Infinite); break;
        }
        return new($"pid={Environment.ProcessId};model={_loaded};selected={SelectedModelId};accel={_acceleration};length={length};detail={detail}", language, 1)
        {
            TokenTimings = [new VocabularyTokenTiming("probe", 0.25, 0.5)],
            Segments = [new PluginTranscriptionSegment("probe", 0, 1)]
        };
    }
}
