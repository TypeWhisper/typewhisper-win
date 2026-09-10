using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class VocabularyLifecycleTests
{
    private static Task<VocabularyOutcome> Run(VocabularyPluginSession session, CancellationToken cancellation = default) =>
        session.RefineAsync(Guid.NewGuid(), "type whisper", new float[16000], 16000,
            [new("type whisper", 0, 1)], [new("TypeWhisper")], cancellation);

    [Fact]
    public async Task DisabledSessionDoesNotLoadOrInvokePlugin()
    {
        await using var session = new VocabularyPluginSession(() => throw new Exception("Should not load"));
        Assert.Equal("type whisper", (await Run(session)).Text);
    }

    [Fact]
    public async Task DisableDrainsNativeRequestAndRejectsLateResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = Lease(async (request, _) =>
        {
            entered.SetResult(); await finish.Task;
            return new(request.RecordingId, [new(0, 12, "TypeWhisper", 1)]);
        });
        await using var session = new VocabularyPluginSession(() => Task.FromResult<IVocabularyPluginLease>(lease));
        await session.SetEnabledAsync(true);
        var result = Run(session); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disable = session.SetEnabledAsync(false);
        Assert.False(session.Enabled);
        Assert.False(disable.IsCompleted);
        Assert.False(lease.Disposed);
        finish.SetResult();
        Assert.Equal("type whisper", (await result).Text);
        await disable;
        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task DisableDuringActivationDisposesNewModelWithoutEnablingIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = Lease((r, _) => Task.FromResult(new VocabularyRescoreResult(r.RecordingId, [])));
        await using var session = new VocabularyPluginSession(async () => { entered.SetResult(); await finish.Task; return lease; });
        var enable = session.SetEnabledAsync(true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disable = session.SetEnabledAsync(false);
        finish.SetResult(); await Task.WhenAll(enable, disable);
        Assert.False(session.Enabled); Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task CallerCancellationPropagatesButModelRemainsLoaded()
    {
        using var cancellation = new CancellationTokenSource();
        var lease = Lease((request, _) =>
        { cancellation.Cancel(); return Task.FromResult(new VocabularyRescoreResult(request.RecordingId, [])); });
        await using var session = new VocabularyPluginSession(() => Task.FromResult<IVocabularyPluginLease>(lease));
        await session.SetEnabledAsync(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(session, cancellation.Token));
        Assert.True(session.Enabled); Assert.False(lease.Disposed);
    }

    [Fact]
    public async Task ActivationFailureStaysDisabledAndCanBeRetried()
    {
        var attempt = 0;
        var lease = Lease((r, _) => Task.FromResult(new VocabularyRescoreResult(r.RecordingId, [])));
        await using var session = new VocabularyPluginSession(() => ++attempt == 1
            ? throw new IOException("Missing model") : Task.FromResult<IVocabularyPluginLease>(lease));
        await Assert.ThrowsAsync<IOException>(() => session.SetEnabledAsync(true));
        Assert.False(session.Enabled);
        await session.SetEnabledAsync(true); Assert.True(session.Enabled);
    }

    [Fact]
    public async Task DisposeIsIdempotentAndRejectsFurtherActivation()
    {
        var lease = Lease((r, _) => Task.FromResult(new VocabularyRescoreResult(r.RecordingId, [])));
        var session = new VocabularyPluginSession(() => Task.FromResult<IVocabularyPluginLease>(lease));
        await session.SetEnabledAsync(true);
        await session.DisposeAsync(); await session.DisposeAsync();
        Assert.Equal(1, lease.DisposalCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetEnabledAsync(true));
    }

    private static TestLease Lease(Func<VocabularyRescoreRequest, CancellationToken, Task<VocabularyRescoreResult>> decode)
    {
        var plugin = new Mock<IVocabularyRescorerPlugin>();
        plugin.SetupGet(p => p.IsReady).Returns(true);
        plugin.Setup(p => p.RescoreAsync(It.IsAny<VocabularyRescoreRequest>(), It.IsAny<CancellationToken>())).Returns(decode);
        return new(plugin.Object);
    }
    private sealed class TestLease(IVocabularyRescorerPlugin plugin) : IVocabularyPluginLease
    {
        public IVocabularyRescorerPlugin Plugin => plugin;
        public int DisposalCount { get; private set; }
        public bool Disposed => DisposalCount != 0;
        public ValueTask DisposeAsync() { DisposalCount++; return ValueTask.CompletedTask; }
    }
}
