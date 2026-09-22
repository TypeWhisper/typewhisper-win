using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictationStartupTargetTests
{
    [Theory]
    [InlineData(1, 1, 2, 10u, 10u, true)]
    [InlineData(1, 2, 2, 10u, 10u, true)]
    [InlineData(1, 3, 2, 10u, 10u, false)]
    [InlineData(1, 2, 2, 10u, 11u, false)]
    [InlineData(1, 2, 2, 10u, 0u, false)]
    [InlineData(1, 0, 0, 10u, 10u, false)]
    [InlineData(0, 0, 2, 10u, 10u, false)]
    public void OnlyOriginalTargetOrOwnedTrayCanRetainCapture(int target, int foreground, int tray, uint expected, uint current, bool valid) =>
        Assert.Equal(valid, DictationStartupTarget.IsValid(target, foreground, tray, expected, current));
}
