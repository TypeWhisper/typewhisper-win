using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowPaletteShortcutTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RunningWorkAndShutdownPreventThePalette(bool closing, bool busy)
        => Assert.NotNull(WorkflowPaletteShortcutAdmission.Rejection(closing, busy));

    [Fact]
    public void IdleAppOpensThePalette()
        => Assert.Null(WorkflowPaletteShortcutAdmission.Rejection(false, false));
}
