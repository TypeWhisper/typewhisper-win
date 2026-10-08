using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public sealed class DelayedInputTests
{
    [Theory]
    [InlineData(0u, 0L, false)]
    [InlineData(10u, 510L, false)]
    [InlineData(10u, 511L, true)]
    [InlineData(4294967200u, 4294967300L, false)]
    [InlineData(4294966500u, 4294967300L, true)]
    public void NativeEventAgeHandlesUptimeWrap(uint timestamp, long now, bool stale) =>
        Assert.Equal(stale, InputEventTiming.IsStale(timestamp, now));

    [Fact]
    public void DelayedChordCannotStartUntilAllKeysAreReleased()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        Assert.Null(state.Key(0xA2, true, 0, bindings, stale: true));
        Assert.Null(state.Key(0xA0, true, 10, bindings));
        Assert.Null(state.Key(0xA0, false, 20, bindings));
        Assert.Null(state.Key(0xA2, false, 30, bindings));
        Assert.Null(state.Key(0xA2, true, 40, bindings));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 50, bindings));
    }

    [Fact]
    public void DelayedReleaseStillStopsAnExistingHoldRecording()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings, mode: RecordingMode.Hold);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 10, bindings, mode: RecordingMode.Hold));
        Assert.Equal(HybridHotkeyAction.Stop, state.Key(0xA0, false, 100, bindings, recording: true, mode: RecordingMode.Hold, stale: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatcherStallDropsStartsButPreservesStops(bool recording)
    {
        var clock = new Clock();
        var queue = new Queue<Action>();
        var initiallyRecording = recording;
        var starts = 0;
        var stops = 0;
        using var coordinator = new DictationInputCoordinator(
            () => { starts++; recording = true; return Task.CompletedTask; },
            () => { stops++; recording = false; return Task.CompletedTask; },
            () => Task.CompletedTask, () => recording, () => true, () => RecordingMode.Toggle,
            dispatch: action => { queue.Enqueue(action); return true; }, timeProvider: clock);
        var pending = coordinator.SubmitAsync(DictationInputAction.Toggle);
        clock.Milliseconds = 2000;
        queue.Dequeue()();
        await pending;
        Assert.Equal(0, starts);
        Assert.Equal(initiallyRecording ? 1 : 0, stops);
        Assert.False(coordinator.IsRecordingOrStarting);
        // A fresh gesture works immediately after discarding a delayed start.
        var fresh = coordinator.SubmitAsync(DictationInputAction.Start);
        queue.Dequeue()();
        await fresh;
        Assert.Equal(1, starts);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
