using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ApplicationInstallationTests
{
    [Fact]
    public void FeedsRetainTheInstalledPackageIdentity()
    {
        var original = ApplicationInstallation.Resolve("TypeWhisper")!;
        var separate = ApplicationInstallation.Resolve("TypeWhisperDaily")!;
        Assert.Equal("TypeWhisper.exe", original.Executable);
        Assert.Equal(original.Executable, separate.Executable);
        // The original installation continues the 1.0 feeds, so 1.0 installations upgrade in place.
        Assert.Equal("win-x64", original.Feed(AppUpdateChannel.Stable));
        Assert.Equal("win-x64-rc", original.Feed(AppUpdateChannel.ReleaseCandidate));
        Assert.Equal("win-x64-daily", original.Feed(AppUpdateChannel.Daily));
        Assert.Equal("win-x64-winui-daily", separate.Feed(AppUpdateChannel.Daily));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => separate.Feed(AppUpdateChannel.Stable));
        Assert.Throws<ArgumentOutOfRangeException>(() => separate.Feed(AppUpdateChannel.ReleaseCandidate));
    }

    [Fact]
    public void UnknownInstallationsNeverGetAnUpdatePolicy() => Assert.Null(ApplicationInstallation.Resolve("OtherApp"));
}
