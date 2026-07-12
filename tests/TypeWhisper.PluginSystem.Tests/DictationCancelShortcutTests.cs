using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class DictationCancelShortcutTests
{
    [Fact]
    public void CancelShortcut_IsEnabledWhileRecording()
    {
        Assert.True(DictationViewModel.ShouldEnableCancelShortcut(
            isRecording: true,
            pendingJobCount: 0));
    }

    [Fact]
    public void CancelShortcut_IsEnabledWhileJobsArePending()
    {
        Assert.True(DictationViewModel.ShouldEnableCancelShortcut(
            isRecording: false,
            pendingJobCount: 1));
    }

    [Fact]
    public void CancelShortcut_IsDisabledWhenIdle()
    {
        Assert.False(DictationViewModel.ShouldEnableCancelShortcut(
            isRecording: false,
            pendingJobCount: 0));
    }

    [Theory]
    [InlineData(DictationState.Recording)]
    [InlineData(DictationState.Processing)]
    public void CancelConfirmation_RequiresTwoPressesInSameState(DictationState state)
    {
        DictationState? confirmationState = null;

        Assert.False(DictationViewModel.ConfirmCancel(state, ref confirmationState));
        Assert.Equal(state, confirmationState);
        Assert.True(DictationViewModel.ConfirmCancel(state, ref confirmationState));
        Assert.Null(confirmationState);
    }

    [Fact]
    public void CancelConfirmation_StateChangeRequiresTwoNewPresses()
    {
        DictationState? confirmationState = null;
        Assert.False(DictationViewModel.ConfirmCancel(
            DictationState.Recording,
            ref confirmationState));

        DictationViewModel.ResetCancelConfirmation(
            DictationState.Processing,
            ref confirmationState);

        Assert.Null(confirmationState);
        Assert.False(DictationViewModel.ConfirmCancel(
            DictationState.Processing,
            ref confirmationState));
    }
}
