using TypeWhisper.WinUI.Platform;

public sealed class StoreProductIdentityTests
{
    [Fact]
    public void PublicBetaUsesASeparateReservedProductAndPackageCache()
    {
        var stable = AppDistribution.LoadStoreIdentity(beta: false);
        var beta = AppDistribution.LoadStoreIdentity(beta: true);
        Assert.Equal("TypeWhisper.TypeWhisper", stable.PackageIdentityName);
        Assert.Equal("9PF42ZCR0JR0", stable.StoreProductId);
        Assert.Equal("TypeWhisper.TypeWhisperBeta", beta.PackageIdentityName);
        Assert.Equal("9N183R19SQQ8", beta.StoreProductId);
        Assert.Equal(stable.PackagePublisher, beta.PackagePublisher);
        Assert.NotEqual(stable.PackageFamilyName, beta.PackageFamilyName);
        Assert.Equal("ms-windows-store://pdp/?productid=9N183R19SQQ8", beta.StoreProtocolLink);

        var local = Path.Combine(Path.GetTempPath(), "store-profile-test");
        var path = Path.Combine(local, "TypeWhisper-WinUI-StoreBeta", "history.json");
        Assert.Equal(Path.Combine(local, "Packages", beta.PackageFamilyName, "LocalCache", "Local", "TypeWhisper-WinUI-StoreBeta", "history.json"),
            AppDistribution.ResolveShellVisiblePath(path, AppDistributionKind.Store, local, beta.PackageFamilyName));
    }
}
