using TypeWhisper.WinUI;
using Xunit;

public sealed class WinUIProfileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalProfileKeepsExistingLocation(string? name)
    {
        Assert.Equal(Path.Combine("local", "TypeWhisper-WinUI-DevUserData"), WinUIProfile.ResolveRoot(name, "local", "temp"));
    }

    [Fact]
    public void NamedSmokeProfileLivesOnlyInTemporaryDirectory()
    {
        Assert.Equal(Path.Combine("temp", "TypeWhisper-WinUI-TestProfiles", "smoke-1_a"),
            WinUIProfile.ResolveRoot("smoke-1_a", "local", "temp"));
    }

    [Theory]
    [InlineData("../profile")]
    [InlineData("..\\profile")]
    [InlineData("C:\\profile")]
    [InlineData("/profile")]
    [InlineData(" ")]
    [InlineData("ä")]
    public void ProfileNamesCannotRedirectToExistingUserData(string name)
    {
        Assert.Throws<ArgumentException>(() => WinUIProfile.ResolveRoot(name, "local", "temp"));
    }
}
