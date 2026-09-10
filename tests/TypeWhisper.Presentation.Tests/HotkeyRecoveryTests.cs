using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public class HotkeyRecoveryTests
{
    [Theory]
    [InlineData(0x218, 4, false)]
    [InlineData(0x218, 7, true)]
    [InlineData(0x218, 18, true)]
    [InlineData(0x2B1, 7, false)]
    [InlineData(0x2B1, 8, true)]
    [InlineData(0x2B1, 3, true)]
    public void RecognizesPowerAndSessionTransitions(int message, int reason, bool resume)
        => Assert.Equal(resume ? HotkeyRecoverySignal.Resume : HotkeyRecoverySignal.Suspend,
            HotkeyRecoveryState.Classify((uint)message, reason));

    [Fact]
    public void BatteryChangeDoesNotResetHotkeys()
        => Assert.Equal(HotkeyRecoverySignal.None, HotkeyRecoveryState.Classify(0x218, 10));

    [Fact]
    public void DuplicateResumeThenLockOrShutdownInvalidatesPendingRecovery()
    {
        var state = new HotkeyRecoveryState();
        var resume = state.Invalidate();
        var unlock = state.Invalidate();
        Assert.False(state.IsCurrent(resume));
        Assert.True(state.IsCurrent(unlock));
        state.Invalidate(); // lock again before the timer fires
        Assert.False(state.IsCurrent(unlock));
        var pending = state.Invalidate();
        state.Dispose();
        Assert.False(state.IsCurrent(pending));
    }

    [Theory]
    [InlineData(RecordingMode.Hybrid)]
    [InlineData(RecordingMode.Hold)]
    [InlineData(RecordingMode.Toggle)]
    public void LostKeyUpsDoNotBlockTheNextGesture(RecordingMode mode)
    {
        var state = new HybridHotkeyState();
        var keys = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, keys, mode: mode);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 10, keys, mode: mode));
        state.ResetAfterInterruption([]); // key-ups were lost while asleep
        Assert.Null(state.Key(0xA2, false, 2000, keys, mode: mode));
        state.Key(0xA2, true, 2010, keys, mode: mode);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 2020, keys, mode: mode));
    }

    [Fact]
    public void KeysHeldAtUnlockMustBeReleasedBeforeStarting()
    {
        var state = new HybridHotkeyState();
        var keys = new HashSet<string> { "CTRL+SHIFT" };
        state.ResetAfterInterruption([0xA2, 0xA0]);
        Assert.Null(state.Key(0xA0, true, 0, keys));
        Assert.Null(state.Key(0xA0, false, 10, keys));
        Assert.Null(state.Key(0xA2, false, 20, keys));
        state.Key(0xA2, true, 30, keys);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 40, keys));
    }

    [Fact]
    public void ResumeDoesNotUndoUserPause()
    {
        var state = new HybridHotkeyState();
        var keys = new HashSet<string> { "CTRL+SHIFT" };
        state.ResetAfterInterruption([]);
        Assert.Null(state.Key(0xA2, true, 0, keys, paused: true));
        Assert.Null(state.Key(0xA0, true, 10, keys, paused: true));
    }

    [Fact]
    public async Task PendingStartIsDiscardedOnLock()
    {
        Action? queued = null;
        var starts = 0;
        using var input = new DictationInputCoordinator(() => { starts++; return Task.CompletedTask; },
            () => Task.CompletedTask, () => Task.CompletedTask, () => false, () => true,
            () => RecordingMode.Hybrid, action => { queued = action; return true; });
        var completion = input.SubmitAsync(DictationInputAction.Start);
        input.InterruptPendingGesture();
        queued!();
        await completion;
        Assert.Equal(0, starts);
        Assert.False(input.IsRecordingOrStarting);
    }
}
