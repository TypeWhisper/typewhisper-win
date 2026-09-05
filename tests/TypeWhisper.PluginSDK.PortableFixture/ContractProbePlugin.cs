using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.PortableFixture;

// Contract fixture only. Does not run CTC or change any transcript.
public sealed class ContractProbePlugin : IVocabularyRescorerPlugin
{
    public string PluginId => "test.typewhisper.portable-contract";
    public string PluginName => "Portable SDK contract fixture";
    public string PluginVersion => "1.0.0";
    public bool IsReady { get; private set; }
    private bool _disposed;

    public Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReady) throw new InvalidOperationException("Already active");
        var count = host.GetSetting<int>("activations");
        host.SetSetting("activations", count + 1);
        host.Log(PluginLogLevel.Info, "Portable fixture activated");
        IsReady = true;
        host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }

    public Task<VocabularyRescoreResult> RescoreAsync(VocabularyRescoreRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsReady) throw new InvalidOperationException("Not active");
        if (request.SampleRate != 16000) throw new NotSupportedException("Expected 16 kHz mono PCM");
        return Task.FromResult(new VocabularyRescoreResult(request.RecordingId, []));
    }

    public Task DeactivateAsync() { IsReady = false; return Task.CompletedTask; }
    public void Dispose() { _disposed = true; IsReady = false; }
}
