using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ShortcutAdmissionTests
{
    [Theory]
    [InlineData("recent transcriptions", "Finish the current operation before opening recent transcriptions. Your work is kept intact.")]
    [InlineData("the recorder", "Finish the current operation before opening the recorder. Your work is kept intact.")]
    [InlineData("the workflow palette", "Finish the current operation before opening the workflow palette. Your work is kept intact.")]
    public void RunningWorkKeepsTheShortcutFromOpeningItsWindow(string destination, string message)
        => Assert.Equal(message, ShortcutAdmission.Rejection(destination, operationBusy: true));

    [Fact]
    public void IdleAppOpensTheWindow()
        => Assert.Null(ShortcutAdmission.Rejection("the recorder", operationBusy: false));
}
