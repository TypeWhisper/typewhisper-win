using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class VocabularyActivationCancellationTests
{
    [Fact]
    public async Task LegacyActivationIsDrainedBeforeDefaultOverloadReportsCancellation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        ITypeWhisperPlugin plugin = new LegacyPlugin(release.Task);
        var activation = plugin.ActivateAsync(Mock.Of<IPluginHostServices>(), cancellation.Token);
        cancellation.Cancel();
        Assert.False(activation.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
    }

    [Fact]
    public async Task PreCanceledPackageLoadDoesNotReadOrConstructPackage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PortablePluginPackage.LoadAsync(
            "missing-package-path", Mock.Of<IPluginHostServices>(), new Version(1, 1), cancellation.Token));
    }

    [Fact]
    public async Task DisableCancelsActivationBeforeWaitingAndDrainsLateLease()
    {
        var release = new TaskCompletionSource<IVocabularyPluginLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activationToken = default;
        var lease = new Lease();
        await using var session = new VocabularyPluginSession(ct => { activationToken = ct; return release.Task; });
        var enable = session.SetEnabledAsync(true);
        var disable = session.SetEnabledAsync(false);
        Assert.True(activationToken.IsCancellationRequested);
        Assert.False(disable.IsCompleted);
        release.SetResult(lease);
        await Task.WhenAll(enable, disable);
        Assert.True(lease.Disposed);
        Assert.False(session.Enabled);
    }

    [Fact]
    public async Task DisposeCancelsCooperativeActivationAndCompletesAfterFactoryCleanup()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new VocabularyPluginSession(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { canceled.SetResult(); await cleanup.Task; }
            return new Lease();
        });
        var enable = session.SetEnabledAsync(true);
        var dispose = session.DisposeAsync().AsTask();
        await canceled.Task;
        Assert.False(dispose.IsCompleted);
        cleanup.SetResult();
        await Task.WhenAll(enable, dispose);
        Assert.False(session.Enabled);
    }

    [Fact]
    public async Task ExternalCancellationPropagatesAndLaterActivationCanRetry()
    {
        var attempt = 0;
        await using var session = new VocabularyPluginSession(async ct =>
        {
            if (++attempt == 1) await Task.Delay(Timeout.Infinite, ct);
            return new Lease();
        });
        using var cancellation = new CancellationTokenSource();
        var enable = session.SetEnabledAsync(true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enable);
        Assert.False(session.Enabled);
        await session.SetEnabledAsync(true);
        Assert.True(session.Enabled);
    }

    [Fact]
    public async Task UnrelatedFactoryCancellationIsReportedInsteadOfClaimingSuccessfulActivation()
    {
        await using var session = new VocabularyPluginSession(_ =>
            Task.FromException<IVocabularyPluginLease>(new OperationCanceledException("Factory canceled independently.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SetEnabledAsync(true));
        Assert.False(session.Enabled);
    }

    private sealed class Lease : IVocabularyPluginLease
    {
        public IVocabularyRescorerPlugin Plugin { get; } = Mock.Of<IVocabularyRescorerPlugin>();
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class LegacyPlugin(Task activation) : ITypeWhisperPlugin
    {
        public string PluginId => "test.legacy";
        public string PluginName => "Legacy fixture";
        public string PluginVersion => "1.0.0";
        public Task ActivateAsync(IPluginHostServices host) => activation;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
