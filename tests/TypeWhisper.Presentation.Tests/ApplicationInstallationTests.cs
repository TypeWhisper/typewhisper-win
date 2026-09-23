using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ApplicationInstallationTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void DailyFeedsRetainTheInstalledPackageIdentity(string arch)
    {
        var original = ApplicationInstallation.Resolve("TypeWhisper")!;
        var separate = ApplicationInstallation.Resolve("TypeWhisperDaily")!;
        Assert.Equal("TypeWhisper.exe", original.Executable);
        Assert.Equal(original.Executable, separate.Executable);
        Assert.Equal(arch + "-daily", original.Feed(AppUpdateChannel.Daily, arch));
        Assert.Equal(arch + "-winui-daily", separate.Feed(AppUpdateChannel.Daily, arch));
        Assert.Equal(arch + "-winui-stable", original.Feed(AppUpdateChannel.Stable, arch));
        Assert.Equal(arch + "-winui-rc", original.Feed(AppUpdateChannel.ReleaseCandidate, arch));
        Assert.True(original.Accepts("TypeWhisper", 1, 1));
        Assert.False(original.Accepts("TypeWhisperDaily", 1, 1));
        Assert.False(separate.Accepts("TypeWhisper", 1, 1));
        Assert.False(original.Accepts("TypeWhisper", 1, 0));
    }
    [Fact]
    public void UnknownInstallationsNeverGetAnUpdatePolicy() => Assert.Null(ApplicationInstallation.Resolve("OtherApp"));
}
