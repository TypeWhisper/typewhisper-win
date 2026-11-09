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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
