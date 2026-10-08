using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class TransientRequestRetryTests
{
    private sealed class TransientException(TimeSpan? retryAfter = null) : Exception("transient")
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private readonly List<TimeSpan> _delays = [];
    private readonly List<TransientRequestRetryAttempt> _retries = [];

    private TransientRequestRetry Policy(Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
        ex => ex is TransientException transient ? new(transient.RetryAfter, 429, "RateLimit") : null,
        delay ?? ((wait, _) => { _delays.Add(wait); return Task.CompletedTask; }),
        _retries.Add);

    [Fact]
    public async Task TransientFailureIsRetriedTwiceWithOneThenTwoSeconds()
    {
        var thrown = new List<TransientException>();
        var error = await Assert.ThrowsAsync<TransientException>(() => Policy().RunAsync<string>(_ =>
        {
            thrown.Add(new TransientException());
            throw thrown[^1];
        }, default));

        Assert.Equal(3, thrown.Count);
        Assert.Same(thrown[^1], error);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _delays);
        Assert.Equal([1, 2], _retries.Select(retry => retry.Attempt));
        Assert.Equal(thrown.Take(2), _retries.Select(retry => retry.Error));
        Assert.Equal(429, _retries[0].Failure.StatusCode);
    }

    [Fact]
    public async Task SecondAttemptCanSucceed()
    {
        var attempts = 0;
        var result = await Policy().RunAsync(_ => ++attempts == 1 ? throw new TransientException() : Task.FromResult("ok"), default);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays);
    }

    [Fact]
    public async Task ProviderWaitBelowTheCapIsHonoured()
    {
        var attempts = 0;
        await Policy().RunAsync(_ => ++attempts == 1 ? throw new TransientException(TimeSpan.FromSeconds(5)) : Task.FromResult(1), default);

        Assert.Equal([TimeSpan.FromSeconds(5)], _delays);
    }

    [Fact]
    public async Task ProviderWaitAboveTheCapSurfacesTheError()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<TransientException>(() => Policy().RunAsync<int>(_ =>
        {
            attempts++;
            throw new TransientException(TimeSpan.FromSeconds(11));
        }, default));

        Assert.Equal(1, attempts);
        Assert.Empty(_delays);
        Assert.Empty(_retries);
    }

    [Fact]
    public async Task NonTransientFailureIsNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Policy().RunAsync<int>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("permanent");
        }, default));

        Assert.Equal(1, attempts);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task CancellationDuringTheWaitStopsTheRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var policy = Policy((wait, token) => { cancellation.Cancel(); return Task.Delay(wait, token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.RunAsync<int>(_ =>
        {
            attempts++;
            throw new TransientException();
        }, cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ACancelledRequestIsNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Policy().RunAsync<int>(_ =>
        {
            attempts++;
            throw new OperationCanceledException();
        }, default));

        Assert.Equal(1, attempts);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task AFailureAfterTheCallerGaveUpSurfacesWithoutAWait()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<TransientException>(() => Policy().RunAsync<int>(_ =>
        {
            cancellation.Cancel();
            throw new TransientException();
        }, cancellation.Token));

        Assert.Empty(_delays);
    }

    [Fact]
    public void DelayClampsNegativeWaitsAndLaterAttempts()
    {
        Assert.Equal(TimeSpan.Zero, TransientRequestRetry.DelayFor(new(TimeSpan.FromSeconds(-3)), 1));
        Assert.Equal(TimeSpan.FromSeconds(10), TransientRequestRetry.DelayFor(new(TimeSpan.FromSeconds(10)), 1));
        Assert.Null(TransientRequestRetry.DelayFor(new(TimeSpan.FromSeconds(10.001)), 1));
        Assert.Equal(TimeSpan.FromSeconds(2), TransientRequestRetry.DelayFor(new(), 7));
    }
}
