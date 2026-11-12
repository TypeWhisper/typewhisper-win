using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiAuthenticationDefaultTests
{
    [Theory]
    // Settings from before the default: a running tokenless server keeps working, an off one asks for the token.
    [InlineData(true, false, 0, false)]
    [InlineData(false, false, 0, true)]
    [InlineData(true, true, 0, true)]
    [InlineData(false, true, 0, true)]
    // Current settings keep the user's choice, including turning the token off.
    [InlineData(true, false, 2, false)]
    [InlineData(false, false, 2, false)]
    [InlineData(true, true, 2, true)]
    [InlineData(false, true, 3, true)]
    public void RequiresToken(bool enabled, bool saved, int version, bool expected) =>
        Assert.Equal(expected, LocalApiAuthenticationDefault.RequiresToken(enabled, saved, version));
}
