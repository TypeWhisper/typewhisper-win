using TypeWhisper.Presentation;
using Xunit;

public sealed class KeyboardScrollPolicyTests
{
    [Theory]
    [InlineData(0x28, 0, 400, 1000, 64)]
    [InlineData(0x26, 100, 400, 1000, 36)]
    [InlineData(0x26, 0, 400, 1000, 0)]
    [InlineData(0x28, 990, 400, 1000, 1000)]
    [InlineData(0x22, 0, 400, 1000, 340)]
    [InlineData(0x21, 500, 400, 1000, 160)]
    [InlineData(0x24, 900, 400, 1000, 0)]
    [InlineData(0x23, 0, 400, 1000, 1000)]
    [InlineData(0x28, 0, 400, 0, 0)]
    public void NavigationIsBounded(int key, double offset, double viewport, double max, double expected) =>
        Assert.Equal(expected, KeyboardScrollPolicy.Target(key, offset, viewport, max));

    [Fact]
    public void OtherKeysRemainAvailableToControls() =>
        Assert.Null(KeyboardScrollPolicy.Target(0x41, 100, 400, 1000));
}
