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
}
