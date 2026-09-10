using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowPaletteShortcutTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ExistingWorkAndShutdownPreventNavigation(bool closing, bool busy, bool otherWorkspace)
        => Assert.NotNull(WorkflowPaletteShortcutAdmission.Rejection(closing, busy, otherWorkspace));

    [Fact]
    public void IdleLauncherAndExistingWorkflowCanBeFocused()
        => Assert.Null(WorkflowPaletteShortcutAdmission.Rejection(false, false, false));
}
