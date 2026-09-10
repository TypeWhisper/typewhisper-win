using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ApplicationUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "updates-tests-" + Guid.NewGuid().ToString("N"));
    private AppUpdatePreferences Preferences(string version = "1.1.0-daily.20260910.6") => new(Path.Combine(_root, "updates.json"), version);
    [Theory]
    [InlineData("1.1.0", AppUpdateChannel.Stable)]
    [InlineData("1.1.0-rc.1", AppUpdateChannel.ReleaseCandidate)]
    [InlineData("1.1.0-daily.20260910.6", AppUpdateChannel.Daily)]
    public void InfersInstalledTrackAndPersistsExplicitChoice(string version, AppUpdateChannel expected)
    {
        var preferences = Preferences(version);
        Assert.Equal(expected, preferences.Channel);
        Assert.True(preferences.Save(AppUpdateChannel.ReleaseCandidate));
        Assert.Equal(AppUpdateChannel.ReleaseCandidate, Preferences("9.0.0").Channel);
    }
    [Theory]
    [InlineData(AppUpdateChannel.Stable, "stable")]
    [InlineData(AppUpdateChannel.Daily, "daily")]
    [InlineData(AppUpdateChannel.ReleaseCandidate, "rc")]
    public void FeedsCannotFallBackToLegacyPackages(AppUpdateChannel channel, string suffix)
    {
        foreach (var arch in new[] { "win-x64", "win-arm64" })
            Assert.Equal(arch + "-winui-" + suffix, AppUpdatePreferences.Feed(channel, arch));
        Assert.Throws<ArgumentException>(() => AppUpdatePreferences.Feed(channel, "win-x86"));
    }
    [Fact]
    public void FailedSavePreservesSelection()
    {
        var preferences = Preferences();
        Directory.CreateDirectory(Path.Combine(_root, "updates.json"));
        Assert.False(preferences.Save(AppUpdateChannel.Stable));
        Assert.Equal(AppUpdateChannel.Daily, preferences.Channel);
        Assert.NotNull(preferences.Error);
    }
    [Fact]
    public async Task SwitchingChannelsInvalidatesOfferAndRequiresNewCheck()
    {
        var backend = new Backend();
        var controller = new AppUpdateController(Preferences(), backend, apply => { apply(); return Task.FromResult<string?>(null); });
        await controller.CheckAsync();
        Assert.NotNull(controller.Offer);
        Assert.True(controller.Select(AppUpdateChannel.Stable));
        Assert.Null(controller.Offer);
        await controller.InstallAsync();
        Assert.DoesNotContain(backend.Calls, call => call != "check");
        await controller.CheckAsync();
        Assert.Equal(AppUpdateChannel.Stable, backend.Channel);
    }
    [Fact]
    public async Task CheckBlocksConcurrentChangesAndRepeatedChecks()
    {
        var gate = new TaskCompletionSource<AppUpdateCheck>();
        var backend = new Backend { Check = () => gate.Task };
        var controller = new AppUpdateController(Preferences(), backend, _ => Task.FromResult<string?>(null));
        var pending = controller.CheckAsync();
        Assert.True(controller.Busy);
        Assert.False(controller.Select(AppUpdateChannel.Stable));
        await controller.CheckAsync();
        Assert.Single(backend.Calls);
        gate.SetResult(new(false, null)); await pending;
        Assert.False(controller.Busy); Assert.Null(controller.Offer);
        Assert.Contains("No compatible", controller.Status);
    }
    [Fact]
    public async Task InstallationDownloadsBeforeShutdownAndApply()
    {
        var backend = new Backend();
        var controller = new AppUpdateController(Preferences(), backend, apply =>
        { backend.Calls.Add("shutdown"); apply(); return Task.FromResult<string?>(null); });
        await controller.CheckAsync(); await controller.InstallAsync();
        Assert.Equal(new[] { "check", "download", "shutdown", "apply" }, backend.Calls);
    }
    [Fact]
    public async Task FailedDownloadNeverShutsDown()
    {
        var backend = new Backend { FailDownload = true };
        var controller = new AppUpdateController(Preferences(), backend, _ => throw new Exception("must not shut down"));
        await controller.CheckAsync(); await controller.InstallAsync();
        Assert.Equal(new[] { "check", "download" }, backend.Calls);
        Assert.Contains("installation failed", controller.Status);
        Assert.False(controller.Busy);
    }
    [Fact]
    public async Task UnavailableHostCanSaveChannelsButCannotInstallOrCheck()
    {
        var backend = new Backend { UnavailableReason = "Development build" };
        var controller = new AppUpdateController(Preferences(), backend, _ => throw new Exception());
        Assert.True(controller.Select(AppUpdateChannel.Stable));
        await controller.CheckAsync(); await controller.InstallAsync();
        Assert.Empty(backend.Calls); Assert.Equal("Development build", controller.Status);
    }
    [Fact]
    public async Task FailedCheckDiscardsEarlierOffer()
    {
        var backend = new Backend();
        var controller = new AppUpdateController(Preferences(), backend, _ => Task.FromResult<string?>(null));
        await controller.CheckAsync();
        backend.Check = () => throw new IOException("offline");
        await controller.CheckAsync();
        Assert.Null(controller.Offer); Assert.Contains("offline", controller.Status);
    }
    private sealed class Backend : IAppUpdateBackend
    {
        public string? UnavailableReason { get; init; }
        public List<string> Calls { get; } = [];
        public AppUpdateChannel Channel;
        public bool FailDownload;
        public Func<Task<AppUpdateCheck>> Check = () => Task.FromResult(new AppUpdateCheck(true, new("1.2.0", false)));
        public Task<AppUpdateCheck> CheckAsync(AppUpdateChannel channel) { Calls.Add("check"); Channel = channel; return Check(); }
        public Task DownloadAsync(AppUpdateOffer offer) { Calls.Add("download"); return FailDownload ? Task.FromException(new IOException("download failed")) : Task.CompletedTask; }
        public void Apply(AppUpdateOffer offer) => Calls.Add("apply");
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
