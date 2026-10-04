using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public sealed class StoreProfileIsolationTests
{
    [Fact]
    public void ResetTargetsMatchTheCompiledDistribution()
    {
#if TYPEWHISPER_STORE_BETA
        Assert.False(WinUIProfile.UsesLegacyData);
        Assert.Empty(UserDataDeletion.PreviousVersionData);
        Assert.EndsWith("TypeWhisper-WinUI-StoreBeta-errors.log", WinUIProfile.LegacyErrorLogPath);
        Assert.Contains("TypeWhisper-Beta", WinUIProfile.CliInstallDirectory);
        Assert.Equal("TypeWhisper Beta", WinUIProfile.DisplayName);
#elif !DEBUG
        Assert.True(WinUIProfile.UsesLegacyData);
        Assert.Equal(2, UserDataDeletion.PreviousVersionData.Length);
        Assert.Null(WinUIProfile.CliInstallDirectory);
#else
        Assert.False(WinUIProfile.UsesLegacyData);
        Assert.Empty(UserDataDeletion.PreviousVersionData);
#endif
    }

    [Fact]
    public void BetaCliDestinationDoesNotReplaceTheStandardInstallation()
    {
        var profile = Path.Combine(Path.GetTempPath(), "store-beta-profile");
        var directory = Path.Combine(Path.GetTempPath(), "store-beta-cli");
        var beta = new CliInstallation(profile, directory);
        Assert.Equal(Path.Combine(directory, "typewhisper.exe"), beta.GetState().InstallPath);
        Assert.NotEqual(new CliInstallation(profile).GetState().InstallPath, beta.GetState().InstallPath);
    }
}
