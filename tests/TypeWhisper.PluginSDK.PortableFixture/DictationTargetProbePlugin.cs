using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>A dictation target for "ProbeGame" whose delivery waits until it is cancelled.</summary>
public sealed class DictationTargetProbePlugin : IDictationTargetPlugin
{
    private IPluginHostServices? _host;

    /// <inheritdoc />
    public string PluginId => "test.typewhisper.target";
    /// <inheritdoc />
    public string PluginName => "Dictation target fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public IReadOnlyList<string> TargetProcessNames => ["ProbeGame", " probegame ", ""];

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _host?.SetSetting("disposals", _host.GetSetting<int>("disposals") + 1);

    /// <inheritdoc />
    public Task<DictationTargetPlan?> PrepareDictationAsync(DictationTargetInfo target, CancellationToken cancellationToken) =>
        Task.FromResult(target.ProcessName == "ProbeGame" ? new DictationTargetPlan { AppName = "Probe Game", TranslateTo = "German" } : null);

    /// <inheritdoc />
    public DateTimeOffset? OnDictationPhaseChanged(Guid dictationId, DictationTargetPhase phase, IDictationTargetInput input) =>
        input.SignalKeys([(ushort)(0x70 + (int)phase)]) ? DateTimeOffset.MaxValue : null;

    /// <inheritdoc />
    public async Task<DictationDeliveryResult> DeliverDictationAsync(Guid dictationId, string text, IDictationTargetInput input,
        CancellationToken cancellationToken)
    {
        _host?.Log(PluginLogLevel.Info, "request-start");
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        finally
        {
            // Returns a little after the cancellation; the package must not be released before.
            await Task.Delay(200, CancellationToken.None);
            input.PressKeys([(ushort)(_host?.GetSetting<int>("disposals") ?? -1)]);
        }
        return new(1, 1);
    }
}
