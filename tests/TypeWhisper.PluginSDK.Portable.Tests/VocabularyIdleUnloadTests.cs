using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSDK.Portable.Tests;

// The CTC rescoring model follows the idle policy of the transcription model (#570): an enabled session
// releases it after the idle delay and loads it again for the next rescoring or when a recording begins.
public sealed class VocabularyIdleUnloadTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly List<TestLease> _leases = [];
    private readonly List<Exception> _failures = [];
    private int _loadsStarted;
    private int _changes;
    private TaskCompletionSource? _loadGate;
    private TaskCompletionSource? _rescoreGate;
    private Exception? _loadFailure;

    private VocabularyPluginSession Create(int seconds = 60)
    {
        var session = new VocabularyPluginSession(LoadAsync, new ModelIdleUnloadPolicy(seconds, _time));
        session.LoadFailed += _failures.Add;
        session.Changed += () => _changes++;
        return session;
    }

    private async Task<IVocabularyPluginLease> LoadAsync(CancellationToken ct)
    {
        _loadsStarted++;
        if (_loadGate is { } gate) await gate.Task.WaitAsync(ct);
        if (_loadFailure is { } failure) throw failure;
        var lease = new TestLease(async (request, token) =>
        {
            if (_rescoreGate is { } rescoring) await rescoring.Task.WaitAsync(token);
            return new(request.RecordingId, [new(0, 12, "TypeWhisper", 1)]);
        });
        _leases.Add(lease);
        return lease;
    }

    private static Task<VocabularyOutcome> Run(VocabularyPluginSession session, CancellationToken cancellation = default) =>
        session.RefineAsync(Guid.NewGuid(), "type whisper", new float[16000], 16000,
            [new("type whisper", 0, 1)], [new("TypeWhisper")], cancellation);

    [Fact]
    public async Task AnIdleModelIsReleasedOnceAndTheSessionStaysEnabled()
    {
        await using var session = Create();
        await session.SetEnabledAsync(true);
        Assert.Equal(1, _changes);
        Assert.Equal("TypeWhisper", (await Run(session)).Text);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(session.Loaded);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Assert.Single(_leases).DisposalCount);
        Assert.True(session.Enabled);
        Assert.False(session.Loaded);
        Assert.Equal(2, _changes);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, Assert.Single(_leases).DisposalCount);
        Assert.Equal(1, _loadsStarted);
    }

    [Fact]
    public async Task ARescoringAfterTheReleaseWaitsForTheLoadAndThenRuns()
    {
        await using var session = Create();
        await session.SetEnabledAsync(true);
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(session.Loaded);
        _loadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var rescoring = Run(session);
        Assert.Equal(2, _loadsStarted);
        Assert.False(rescoring.IsCompleted);
        _loadGate.SetResult();
        Assert.Equal("TypeWhisper", (await rescoring).Text);
        Assert.True(session.Loaded);
        Assert.Equal(2, _leases.Count);
        Assert.Empty(_failures);
    }

    [Fact]
    public async Task PreparingStartsTheLoadAndARescoringAwaitsThatSameLoad()
    {
        await using var session = Create();
        await session.SetEnabledAsync(true);
        _time.Advance(TimeSpan.FromSeconds(60));
        _loadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepare = session.PrepareAsync();
        Assert.Equal(2, _loadsStarted);
        Assert.False(prepare.IsCompleted);
        var rescoring = Run(session);
        Assert.False(rescoring.IsCompleted);
        _loadGate.SetResult();
        await prepare;
        Assert.Equal("TypeWhisper", (await rescoring).Text);
        Assert.Equal(2, _loadsStarted);
        // A loaded model is not prepared again.
        await session.PrepareAsync();
        Assert.Equal(2, _loadsStarted);
    }

    [Fact]
    public async Task AnIdleReleaseWaitsForARunningRescoring()
    {
        await using var session = Create();
        await session.SetEnabledAsync(true);
        _rescoreGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var rescoring = Run(session);
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(session.Loaded);
        Assert.Equal(0, _leases[0].DisposalCount);
        _rescoreGate.SetResult();
        Assert.Equal("TypeWhisper", (await rescoring).Text);
        Assert.True(session.Loaded);
        // The use restarted the full idle wait rather than the shorter retry.
        _time.Advance(ModelIdleTimer.RetryDelay);
        Assert.True(session.Loaded);
        _time.Advance(TimeSpan.FromSeconds(60) - ModelIdleTimer.RetryDelay);
        Assert.False(session.Loaded);
        Assert.Equal(1, _leases[0].DisposalCount);
    }

    [Fact]
    public async Task AFailedReloadKeepsTheTextReportsAndIsTriedAgain()
    {
        await using var session = Create();
        await session.SetEnabledAsync(true);
        _time.Advance(TimeSpan.FromSeconds(60));
        _loadFailure = new IOException("missing model");
        await session.PrepareAsync();
        Assert.Single(_failures);
        var outcome = await Run(session);
        Assert.Equal("type whisper", outcome.Text);
        Assert.NotNull(outcome.Error);
        Assert.Equal(2, _failures.Count);
        Assert.True(session.Enabled);
        Assert.False(session.Loaded);
        _loadFailure = null;
        Assert.Equal("TypeWhisper", (await Run(session)).Text);
        Assert.True(session.Loaded);
    }

    [Fact]
    public async Task DisablingAReleasedSessionNeedsNoModel()
    {
        await using var session = Create();
        await session.SetEnabledAsync(true);
        _time.Advance(TimeSpan.FromSeconds(60));
        await session.SetEnabledAsync(false);
        Assert.False(session.Enabled);
        Assert.False(session.Loaded);
        Assert.Equal("type whisper", (await Run(session)).Text);
        Assert.Equal(1, _loadsStarted);
        await session.PrepareAsync();
        Assert.Equal(1, _loadsStarted);
    }

    [Fact]
    public async Task WithoutAPolicyTheModelStaysLoaded()
    {
        await using var session = new VocabularyPluginSession(LoadAsync);
        await session.SetEnabledAsync(true);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.True(session.Loaded);
        Assert.Equal(0, _leases[0].DisposalCount);
    }

    private sealed class TestLease(Func<VocabularyRescoreRequest, CancellationToken, Task<VocabularyRescoreResult>> decode) : IVocabularyPluginLease
    {
        public IVocabularyRescorerPlugin Plugin { get; } = Rescorer(decode);
        public int DisposalCount { get; private set; }
        public ValueTask DisposeAsync() { DisposalCount++; return ValueTask.CompletedTask; }

        private static IVocabularyRescorerPlugin Rescorer(Func<VocabularyRescoreRequest, CancellationToken, Task<VocabularyRescoreResult>> decode)
        {
            var plugin = new Mock<IVocabularyRescorerPlugin>();
            plugin.SetupGet(p => p.IsReady).Returns(true);
            plugin.Setup(p => p.RescoreAsync(It.IsAny<VocabularyRescoreRequest>(), It.IsAny<CancellationToken>())).Returns(decode);
            return plugin.Object;
        }
    }
}
