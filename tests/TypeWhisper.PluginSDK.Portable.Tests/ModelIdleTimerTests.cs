using TypeWhisper.PluginHost;

public sealed class ModelIdleTimerTests
{
    private readonly ManualTimeProvider _time = new();
    private int _releases;
    private bool _busy;
    private bool _allowed = true;

    private (ModelIdleUnloadPolicy Policy, ModelIdleTimer Timer) Create(int seconds)
    {
        var policy = new ModelIdleUnloadPolicy(seconds, _time) { CanUnload = () => _allowed };
        return (policy, new(policy, () =>
        {
            if (_busy) return Task.FromResult(false);
            _releases++;
            return Task.FromResult(true);
        }));
    }

    [Fact]
    public void AModelIsReleasedOnceAfterTheIdleDelay()
    {
        var (_, timer) = Create(60);
        timer.Touch();
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, _releases);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _releases);
        Assert.False(timer.IsPending);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, _releases);
    }

    [Fact]
    public void EachUseRestartsTheIdleWait()
    {
        var (_, timer) = Create(60);
        timer.Touch();
        _time.Advance(TimeSpan.FromSeconds(50));
        timer.Touch();
        _time.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(0, _releases);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _releases);
    }

    [Fact]
    public void AVetoedOrBusyReleaseIsRetriedShortlyAfterwards()
    {
        var (_, timer) = Create(60);
        _allowed = false;
        timer.Touch();
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, _releases);
        Assert.True(timer.IsPending);
        _allowed = true; _busy = true;
        _time.Advance(ModelIdleTimer.RetryDelay);
        Assert.Equal(0, _releases);
        _busy = false;
        _time.Advance(ModelIdleTimer.RetryDelay);
        Assert.Equal(1, _releases);
    }

    [Fact]
    public void NeverKeepsModelsLoadedUntilTheSettingChanges()
    {
        var (policy, timer) = Create(ModelIdleUnloadPolicy.Never);
        timer.Touch();
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, _releases);
        policy.SetSeconds(120);
        _time.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(1, _releases);
    }

    [Fact]
    public void ChoosingNeverStopsAPendingRelease()
    {
        var (policy, timer) = Create(60);
        timer.Touch();
        policy.SetSeconds(ModelIdleUnloadPolicy.Never);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _releases);
    }

    [Fact]
    public void ImmediatelyReleasesRightAfterUse()
    {
        var (_, timer) = Create(ModelIdleUnloadPolicy.Immediately);
        timer.Touch();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, _releases);
    }

    [Fact]
    public void AnExplicitUnloadOrDisposalForgetsTheModel()
    {
        var (policy, timer) = Create(60);
        timer.Touch();
        timer.Cancel();
        policy.SetSeconds(120);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _releases);
        timer.Touch();
        timer.Dispose();
        timer.Touch();
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _releases);
    }
}
