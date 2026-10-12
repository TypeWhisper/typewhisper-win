using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ApplicationInstallationTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void FeedsRetainTheInstalledPackageIdentity(string arch)
    {
        var original = ApplicationInstallation.Resolve("TypeWhisper")!;
        var separate = ApplicationInstallation.Resolve("TypeWhisperDaily")!;
        Assert.Equal("TypeWhisper.exe", original.Executable);
        Assert.Equal(original.Executable, separate.Executable);
        // The original installation continues the 1.0 feeds, so 1.0 installations upgrade in place.
        Assert.Equal(arch, original.Feed(AppUpdateChannel.Stable, arch));
        Assert.Equal(arch + "-rc", original.Feed(AppUpdateChannel.ReleaseCandidate, arch));
        Assert.Equal(arch + "-daily", original.Feed(AppUpdateChannel.Daily, arch));
        Assert.Equal(arch + "-winui-daily", separate.Feed(AppUpdateChannel.Daily, arch));
        Assert.True(original.Accepts("TypeWhisper", 1, 1));
        Assert.False(original.Accepts("TypeWhisperDaily", 1, 1));
        Assert.False(separate.Accepts("TypeWhisper", 1, 1));
        Assert.False(original.Accepts("TypeWhisper", 1, 0));
    }

    [Fact]
    public void SeparateDailyInstallationOnlyReceivesDailyBuilds()
    {
        var original = ApplicationInstallation.Resolve("TypeWhisper")!;
        var separate = ApplicationInstallation.Resolve("TypeWhisperDaily")!;
        Assert.All(Enum.GetValues<AppUpdateChannel>(), channel => Assert.True(original.Supports(channel)));
        Assert.True(separate.Supports(AppUpdateChannel.Daily));
        Assert.False(separate.Supports(AppUpdateChannel.Stable));
        Assert.False(separate.Supports(AppUpdateChannel.ReleaseCandidate));
        Assert.Throws<ArgumentOutOfRangeException>(() => separate.Feed(AppUpdateChannel.Stable, "win-x64"));
        Assert.Throws<ArgumentOutOfRangeException>(() => separate.Feed(AppUpdateChannel.ReleaseCandidate, "win-x64"));
        Assert.Throws<ArgumentException>(() => original.Feed(AppUpdateChannel.Stable, "win-x86"));
    }

    [Fact]
    public void UnknownInstallationsNeverGetAnUpdatePolicy() => Assert.Null(ApplicationInstallation.Resolve("OtherApp"));
}
