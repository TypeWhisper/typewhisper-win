using TypeWhisper.Plugin.Shared;
using Xunit;

namespace PortableMigration.Tests;

// Linked into the test project of every plugin that links plugins/shared/ModelCatalogAutoRefresh.cs.
public sealed class ModelCatalogAutoRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    // The test project sets the switch, so a plugin created through its public constructor stays offline.
    [Fact]
    public void TheTestProcessTurnsTheAutomaticRefreshOff() =>
        Assert.False(ModelCatalogAutoRefresh.AllowedInThisProcess);

    [Fact]
    public void IsStale_TreatsMissingOldAndFutureDatesAsStale()
    {
        using var sut = new ModelCatalogAutoRefresh(enabled: true, new FixedClock(Now));

        Assert.True(sut.IsStale(null));
        Assert.True(sut.IsStale(Now - ModelCatalogAutoRefresh.MaximumAge));
        Assert.True(sut.IsStale(Now.AddMinutes(5)));
        Assert.False(sut.IsStale(Now - ModelCatalogAutoRefresh.MaximumAge + TimeSpan.FromMinutes(1)));
        Assert.False(sut.IsStale(Now));
    }

    [Fact]
    public async Task Start_DoesNothingWhenDisabled()
    {
        using var sut = new ModelCatalogAutoRefresh(enabled: false);
        var ran = false;

        Assert.False(sut.Start(_ => { ran = true; return Task.CompletedTask; }, _ => { }));
        await sut.Current;

        Assert.False(ran);
    }

    [Fact]
    public async Task Start_CancelsTheRunningRefresh()
    {
        using var sut = new ModelCatalogAutoRefresh(enabled: true);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        sut.Start(async ct =>
        {
            firstToken = ct;
            firstStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, _ => { });
        await firstStarted.Task;
        var first = sut.Current;

        var secondRan = false;
        sut.Start(_ => { secondRan = true; return Task.CompletedTask; }, _ => { });
        await first;
        await sut.Current;

        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(secondRan);
    }

    [Fact]
    public async Task Cancel_CancelsTheTokenWithoutReportingAFailure()
    {
        using var sut = new ModelCatalogAutoRefresh(enabled: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reported = null;
        sut.Start(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, ex => reported = ex);
        await started.Task;

        sut.Cancel();
        await sut.Current;

        Assert.Null(reported);
    }

    [Fact]
    public async Task Start_ReportsAFailureAndCompletes()
    {
        using var sut = new ModelCatalogAutoRefresh(enabled: true);
        Exception? reported = null;

        sut.Start(_ => throw new HttpRequestException("offline"), ex => reported = ex);
        await sut.Current;

        Assert.IsType<HttpRequestException>(reported);
    }

    [Fact]
    public async Task Start_SwallowsAThrowingFailureHandler()
    {
        using var sut = new ModelCatalogAutoRefresh(enabled: true);

        sut.Start(_ => throw new HttpRequestException("offline"), _ => throw new InvalidOperationException());

        await sut.Current;
    }

    [Fact]
    public async Task StartRechecks_ChecksEveryIntervalAndSkipsWhileARefreshRuns()
    {
        var clock = new RecheckTestClock(Now);
        using var sut = new ModelCatalogAutoRefresh(enabled: true, clock);
        var checks = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.StartRechecks(() =>
        {
            checks++;
            sut.Start(_ => release.Task, _ => { });
        });

        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval - TimeSpan.FromMinutes(1));
        Assert.Equal(0, checks);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, checks);

        // The refresh that check started still runs, so the next check leaves it alone.
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        Assert.Equal(1, checks);

        release.SetResult();
        await sut.Current;
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        Assert.Equal(2, checks);
    }

    [Fact]
    public void StopRechecksAndDispose_StopTheTimer()
    {
        var clock = new RecheckTestClock(Now);
        var checks = 0;
        var sut = new ModelCatalogAutoRefresh(enabled: true, clock);

        sut.StartRechecks(() => checks++);
        sut.StopRechecks();
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        Assert.Equal(0, clock.ActiveTimers);

        sut.StartRechecks(() => checks++);
        sut.Dispose();
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);

        Assert.Equal(0, checks);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void StartRechecks_DoesNothingWhenDisabled()
    {
        var clock = new RecheckTestClock(Now);
        using var sut = new ModelCatalogAutoRefresh(enabled: false, clock);

        sut.StartRechecks(() => throw new InvalidOperationException("No check expected."));
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void StartRechecks_ReplacesTheEarlierTimerAndSurvivesAThrowingCheck()
    {
        var clock = new RecheckTestClock(Now);
        using var sut = new ModelCatalogAutoRefresh(enabled: true, clock);
        var checks = 0;

        sut.StartRechecks(() => checks += 100);
        sut.StartRechecks(() =>
        {
            checks++;
            throw new InvalidOperationException("A failing check must not stop the timer.");
        });
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);

        Assert.Equal(1, clock.ActiveTimers);
        Assert.Equal(2, checks);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// A clock whose time and timers move only when a test advances it. Plugin tests use it to run the hourly
/// rechecks of <see cref="ModelCatalogAutoRefresh"/> without waiting.
/// </summary>
internal sealed class RecheckTestClock(DateTimeOffset now) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = now;

    public int ActiveTimers
    {
        get { lock (_gate) return _timers.Count; }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>Moves the time forward and runs each timer that came due once, on the calling thread.</summary>
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due <= _now).ToArray();
        }

        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state, _now + dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(RecheckTestClock clock, TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period)
        : ITimer
    {
        public DateTimeOffset Due { get; private set; } = due;

        public void Fire()
        {
            lock (clock._gate)
            {
                if (!clock._timers.Contains(this))
                    return;
                Due += period;
            }

            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (clock._gate)
            {
                Due = clock._now + dueTime;
                period = newPeriod;
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
