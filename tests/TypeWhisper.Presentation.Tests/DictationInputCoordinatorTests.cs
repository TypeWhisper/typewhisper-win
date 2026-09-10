using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public sealed class DictationInputCoordinatorTests
{
    private sealed class Session
    {
        internal readonly TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource StartBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? FinishBarrier;
        internal bool Recording, Processing;
        internal bool Ready = true;
        internal int Starts, Stops, Cancels;
        internal RecordingMode Mode = RecordingMode.Hold;
        internal DictationInputCoordinator Coordinator(Func<Action, bool>? dispatch = null) => new(
            Start, Stop, Cancel, () => Recording, () => Ready && !Processing, () => Mode, dispatch);
        private async Task Start()
        {
            Starts++; StartEntered.TrySetResult();
            await StartBarrier.Task;
            Recording = true;
        }
        private async Task Stop()
        {
            Stops++; Recording = false; Processing = true;
            if (FinishBarrier is not null) await FinishBarrier.Task;
            Processing = false;
        }
        private Task Cancel() { Cancels++; Recording = false; return Task.CompletedTask; }
    }

    [Fact]
    public async Task HoldReleaseWaitsForActualStartAndThenStopsExactlyOnce()
    {
        var session = new Session(); using var input = session.Coordinator();
        var starting = input.SubmitAsync(DictationInputAction.Start);
        await session.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(input.IsRecordingOrStarting);
        var release = input.SubmitAsync(DictationInputAction.Stop);
        Assert.Same(starting, release);
        Assert.Equal(0, session.Stops);
        session.StartBarrier.SetResult();
        await release.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.Starts); Assert.Equal(1, session.Stops);
        Assert.False(session.Recording); Assert.False(input.IsRecordingOrStarting);
    }

    [Fact]
    public async Task SpeculativeCancelSupersedesReleaseAndCannotTurnIntoTranscription()
    {
        var session = new Session(); using var input = session.Coordinator();
        var starting = input.SubmitAsync(DictationInputAction.Start);
        await session.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = input.SubmitAsync(DictationInputAction.Stop);
        _ = input.SubmitAsync(DictationInputAction.Cancel);
        _ = input.SubmitAsync(DictationInputAction.Stop);
        session.StartBarrier.SetResult();
        await starting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.Cancels); Assert.Equal(0, session.Stops); Assert.False(session.Recording);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeChangeOrDisposalDuringStartStillCancelsOwnedCapture(bool dispose)
    {
        var session = new Session(); using var input = session.Coordinator();
        var starting = input.SubmitAsync(DictationInputAction.Start);
        await session.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) input.Dispose();
        else
        {
            session.Mode = RecordingMode.Toggle; input.ObserveMode();
            session.Mode = RecordingMode.Hold; input.ObserveMode();
        }
        session.StartBarrier.SetResult();
        await starting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.Cancels); Assert.False(session.Recording); Assert.Equal(0, session.Stops);
    }

    [Fact]
    public async Task StartsDuringLongFinalProcessingAreDiscardedInsteadOfQueued()
    {
        var session = new Session { FinishBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var input = session.Coordinator();
        session.StartBarrier.SetResult();
        await input.SubmitAsync(DictationInputAction.Start);
        var finishing = input.SubmitAsync(DictationInputAction.Stop);
        Assert.True(session.Processing);
        for (var index = 0; index < 100; index++)
        {
            _ = input.SubmitAsync(DictationInputAction.Start);
            _ = input.SubmitAsync(DictationInputAction.Toggle);
            _ = input.SubmitAsync(DictationInputAction.Cancel);
        }
        session.FinishBarrier.SetResult();
        await finishing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.Starts); Assert.Equal(1, session.Stops); Assert.Equal(0, session.Cancels);
        Assert.False(session.Recording);
        await input.SubmitAsync(DictationInputAction.Start);
        Assert.Equal(2, session.Starts); Assert.True(session.Recording);
    }

    [Fact]
    public void ExternalProcessingIsRejectedAtInputTimeBeforeDispatcherQueue()
    {
        var queued = new Queue<Action>();
        var session = new Session { Processing = true };
        using var input = session.Coordinator(action => { queued.Enqueue(action); return true; });
        _ = input.SubmitAsync(DictationInputAction.Start);
        Assert.Empty(queued);
        session.Processing = false;
        Assert.Equal(0, session.Starts); Assert.False(input.IsRecordingOrStarting);
    }

    [Fact]
    public async Task DelayedDispatcherStartRechecksReadinessAndDoesNotWaitForIt()
    {
        var queued = new Queue<Action>(); var session = new Session();
        using var input = session.Coordinator(action => { queued.Enqueue(action); return true; });
        var pending = input.SubmitAsync(DictationInputAction.Start);
        Assert.True(input.IsRecordingOrStarting);
        session.Processing = true;
        queued.Dequeue()();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        session.Processing = false;
        Assert.Equal(0, session.Starts); Assert.Empty(queued); Assert.False(input.IsRecordingOrStarting);
    }

    [Theory]
    [InlineData(DictationInputAction.Cancel)]
    [InlineData(DictationInputAction.Stop)]
    public async Task TerminalIntentBeforeDispatchIsBoundedAndNeverStartsTwice(DictationInputAction terminal)
    {
        var queued = new Queue<Action>(); var session = new Session();
        using var input = session.Coordinator(action => { queued.Enqueue(action); return true; });
        var pending = input.SubmitAsync(DictationInputAction.Start);
        for (var index = 0; index < 100; index++) _ = input.SubmitAsync(DictationInputAction.Start);
        _ = input.SubmitAsync(terminal);
        Assert.Single(queued);
        session.StartBarrier.SetResult(); queued.Dequeue()();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(terminal == DictationInputAction.Cancel ? 0 : 1, session.Starts);
        Assert.Equal(terminal == DictationInputAction.Stop ? 1 : 0, session.Stops);
        Assert.False(session.Recording);
    }

    [Fact]
    public async Task ModeChangeBeforeDispatchInvalidatesReservation()
    {
        var queued = new Queue<Action>(); var session = new Session();
        using var input = session.Coordinator(action => { queued.Enqueue(action); return true; });
        var pending = input.SubmitAsync(DictationInputAction.Start);
        session.Mode = RecordingMode.Toggle; input.ObserveMode();
        queued.Dequeue()(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, session.Starts); Assert.False(session.Recording);
    }

    [Fact]
    public async Task ToggleStateUsesReservedStartBeforeDispatchForSecondPress()
    {
        var queued = new Queue<Action>(); var session = new Session { Mode = RecordingMode.Toggle };
        using var input = session.Coordinator(action => { queued.Enqueue(action); return true; });
        var keys = new HybridHotkeyState(); var bindings = new HashSet<string> { "CTRL+SHIFT" };
        HybridHotkeyAction? Key(int code, bool down, int time) => keys.Key(code, down, time, bindings, input.IsRecordingOrStarting, session.Mode);
        Key(0xA2, true, 0);
        Assert.Equal(HybridHotkeyAction.Start, Key(0xA0, true, 10));
        var pending = input.SubmitAsync(DictationInputAction.Start);
        Key(0xA0, false, 20); Key(0xA2, false, 30); Key(0xA2, true, 40);
        Assert.Equal(HybridHotkeyAction.Stop, Key(0xA0, true, 50));
        _ = input.SubmitAsync(DictationInputAction.Stop);
        session.StartBarrier.SetResult(); queued.Dequeue()();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.Starts); Assert.Equal(1, session.Stops); Assert.False(session.Recording);
    }

    [Fact]
    public async Task RefusedDispatchDoesNotLeaveAStuckStartingIntent()
    {
        var session = new Session(); using var input = session.Coordinator(_ => false);
        await input.SubmitAsync(DictationInputAction.Start);
        Assert.Equal(0, session.Starts); Assert.False(input.IsRecordingOrStarting);
    }

    [Fact]
    public async Task DisposalBeforeDispatchDropsReservedStartAndRejectsLaterInput()
    {
        var queued = new Queue<Action>(); var session = new Session();
        var input = session.Coordinator(action => { queued.Enqueue(action); return true; });
        var pending = input.SubmitAsync(DictationInputAction.Start);
        input.Dispose();
        _ = input.SubmitAsync(DictationInputAction.Start);
        Assert.Single(queued);
        queued.Dequeue()(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, session.Starts); Assert.False(input.IsRecordingOrStarting); Assert.Empty(queued);
    }

    [Fact]
    public async Task FailedStartDoesNotExecutePendingStopAndAllowsANewGesture()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>(); var starts = 0; var stops = 0;
        using var input = new DictationInputCoordinator(async () => { starts++; await barrier.Task; throw new InvalidOperationException("start failed"); },
            () => { stops++; return Task.CompletedTask; }, () => Task.CompletedTask,
            () => false, () => true, () => RecordingMode.Hold, reportError: errors.Add);
        var pending = input.SubmitAsync(DictationInputAction.Start);
        _ = input.SubmitAsync(DictationInputAction.Stop);
        barrier.SetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stops); Assert.Single(errors); Assert.False(input.IsRecordingOrStarting);
        await input.SubmitAsync(DictationInputAction.Start);
        Assert.Equal(2, starts); Assert.Equal(2, errors.Count);
    }
}
