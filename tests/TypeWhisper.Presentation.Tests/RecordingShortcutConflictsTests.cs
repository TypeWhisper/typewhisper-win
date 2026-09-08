using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class RecordingShortcutConflictsTests
{
    [Theory]
    [InlineData("CTRL+SHIFT", true, "CTRL+SHIFT+R", false, true)]
    [InlineData("CTRL+SHIFT+R", false, "CTRL+SHIFT", true, true)]
    [InlineData("CTRL+SHIFT", true, "CTRL+SHIFT", true, true)]
    [InlineData("CTRL+SHIFT+R", true, "CTRL+SHIFT+T", true, false)]
    [InlineData("", true, "CTRL+SHIFT", true, false)]
    public void RecordingBindingsRejectConflictsInEitherDirection(string first, bool firstModifiers, string second, bool secondModifiers, bool expected)
        => Assert.Equal(expected, RecordingShortcutConflicts.Overlap(first, firstModifiers, second, secondModifiers));

    [Fact]
    public void RecorderDoesNotReplaceAnotherWorkspaceOrInterruptRecording()
    {
        Assert.NotNull(RecorderShortcutAdmission.Rejection(false, true, false));
        Assert.NotNull(RecorderShortcutAdmission.Rejection(false, false, true));
        Assert.Null(RecorderShortcutAdmission.Rejection(false, false, false));
    }
}
