using System.Runtime.InteropServices;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void StoreIdentity_UsesReservedPartnerCenterValues()
    {
        var identity = AppDistribution.StoreIdentity;

        Assert.Equal("TypeWhisper.TypeWhisper", identity.PackageIdentityName);
        Assert.Equal("CN=C90DFED3-0D3C-493E-8620-903C9B1A1D75", identity.PackagePublisher);
        Assert.Equal("TypeWhisper", identity.PublisherDisplayName);
        Assert.Equal("TypeWhisper.TypeWhisper_51tqb5623pxja", identity.PackageFamilyName);
        Assert.Equal("9PF42ZCR0JR0", identity.StoreProductId);
        Assert.Equal("ms-windows-store://pdp/?productid=9PF42ZCR0JR0", identity.StoreProtocolLink);
    }

    [Fact]
    public void IsManagedByMicrosoftStore_OnlyTrueForStoreDistribution()
    {
        Assert.True(UpdateService.IsManagedByMicrosoftStore(AppDistributionKind.Store));
        Assert.False(UpdateService.IsManagedByMicrosoftStore(AppDistributionKind.Direct));
    }

    [Theory]
    [InlineData("0.7.1-daily.20260423", ReleaseChannel.Daily)]
    [InlineData("0.7.1-DAILY.20260423+build", ReleaseChannel.Daily)]
    [InlineData("0.7.0-rc1", ReleaseChannel.ReleaseCandidate)]
    [InlineData("0.7.0-rc.2", ReleaseChannel.ReleaseCandidate)]
    [InlineData("0.7.0", ReleaseChannel.Stable)]
    [InlineData("0.7.0-preview.1", ReleaseChannel.Stable)]
    [InlineData("", ReleaseChannel.Stable)]
    public void InferReleaseChannel_UsesVersionPrereleaseTrack(string version, ReleaseChannel expected)
    {
        Assert.Equal(expected, UpdateService.InferReleaseChannel(version));
    }

    [Theory]
    [InlineData(Architecture.X64, ReleaseChannel.Stable, "win-x64")]
    [InlineData(Architecture.X64, ReleaseChannel.ReleaseCandidate, "win-x64-rc")]
    [InlineData(Architecture.X64, ReleaseChannel.Daily, "win-x64-daily")]
    [InlineData(Architecture.Arm64, ReleaseChannel.Stable, "win-arm64")]
    [InlineData(Architecture.Arm64, ReleaseChannel.ReleaseCandidate, "win-arm64-rc")]
    [InlineData(Architecture.Arm64, ReleaseChannel.Daily, "win-arm64-daily")]
    public void GetVelopackChannel_CombinesArchitectureAndReleaseTrack(
        Architecture architecture,
        ReleaseChannel channel,
        string expected)
    {
        Assert.Equal(expected, UpdateService.GetVelopackChannel(architecture, channel));
    }

    [Theory]
    [InlineData(ReleaseChannel.Stable, "win-x64", false)]
    [InlineData(ReleaseChannel.ReleaseCandidate, "win-x64-rc", true)]
    [InlineData(ReleaseChannel.Daily, "win-x64-daily", true)]
    public void CreateUpdateOptions_AllowsDowngradeForExplicitPreviewChannelSwitches(
        ReleaseChannel channel,
        string expectedChannel,
        bool expectedAllowDowngrade)
    {
        var options = UpdateService.CreateUpdateOptions(Architecture.X64, channel);

        Assert.Equal(expectedChannel, options.ExplicitChannel);
        Assert.Equal(expectedAllowDowngrade, options.AllowVersionDowngrade);
    }

    [Theory]
    [InlineData("stable", "0.7.0-rc1", ReleaseChannel.Stable)]
    [InlineData("release-candidate", "0.7.0", ReleaseChannel.ReleaseCandidate)]
    [InlineData("rc", "0.7.0", ReleaseChannel.ReleaseCandidate)]
    [InlineData("daily", "0.7.0", ReleaseChannel.Daily)]
    [InlineData(null, "0.7.1-daily.20260423", ReleaseChannel.Daily)]
    [InlineData("", "0.7.0-rc1", ReleaseChannel.ReleaseCandidate)]
    [InlineData("unknown", "0.7.0", ReleaseChannel.Stable)]
    public void ResolveReleaseChannel_UsesSavedPreferenceBeforeInstalledVersion(
        string? configuredChannel,
        string installedVersion,
        ReleaseChannel expected)
    {
        Assert.Equal(expected, UpdateService.ResolveReleaseChannel(configuredChannel, installedVersion));
    }

    [Theory]
    [InlineData(ReleaseChannel.Stable, "stable")]
    [InlineData(ReleaseChannel.ReleaseCandidate, "release-candidate")]
    [InlineData(ReleaseChannel.Daily, "daily")]
    public void ToSettingsValue_UsesStableRawValues(ReleaseChannel channel, string expected)
    {
        Assert.Equal(expected, UpdateService.ToSettingsValue(channel));
    }
}
