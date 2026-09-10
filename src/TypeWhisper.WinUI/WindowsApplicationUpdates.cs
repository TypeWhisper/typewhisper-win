using System.Reflection;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;
using TypeWhisper.Windows.Services;
using Velopack;

namespace TypeWhisper.WinUI;

internal sealed class WindowsApplicationUpdates : IAppUpdateBackend
{
    internal static string CurrentVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "unknown";
    private UpdateManager? _manager;
    private UpdateInfo? _update;
    private AppUpdateOffer? _offer;
    public string? UnavailableReason
    {
        get
        {
#if DEBUG
            return "Update channels are saved here. Update checks and installation are available in the installed Daily app.";
#else
            var locator = Velopack.Locators.VelopackLocator.Current;
            return locator.AppId == "TypeWhisperDaily" && locator.CurrentlyInstalledVersion is not null && !string.IsNullOrWhiteSpace(locator.RootAppDir)
                && string.Equals(Environment.ProcessPath, Path.Combine(locator.RootAppDir!, "current", "TypeWhisper.WinUI.exe"), StringComparison.OrdinalIgnoreCase)
                ? null : "Install TypeWhisper Daily using its installer to check for app updates.";
#endif
        }
    }
    public async Task<AppUpdateCheck> CheckAsync(AppUpdateChannel channel)
    {
        _update = null; _offer = null; _manager = null;
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var feed = AppUpdatePreferences.Feed(channel, arch);
        var source = new AppReleaseGithubSource("https://github.com/TypeWhisper/typewhisper-win", feed, channel != AppUpdateChannel.Stable);
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = feed, AllowVersionDowngrade = true });
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return new(source.HasMatchingRelease != false, null);
            if (update.TargetFullRelease.PackageId != "TypeWhisperDaily")
                throw new InvalidDataException("The release uses an incompatible installation identity.");
            _manager = manager; _update = update;
            _offer = new(update.TargetFullRelease.Version.ToString(), manager.CurrentVersion is { } current && update.TargetFullRelease.Version < current);
            return new(true, _offer);
        }
        catch when (source.HasMatchingRelease == false) { return new(false, null); }
    }
    public async Task DownloadAsync(AppUpdateOffer offer)
    {
        Validate(offer);
        await _manager!.DownloadUpdatesAsync(_update!);
    }
    public void Apply(AppUpdateOffer offer)
    {
        Validate(offer);
        _manager!.ApplyUpdatesAndRestart(_update!.TargetFullRelease);
    }
    private void Validate(AppUpdateOffer offer)
    {
        if (UnavailableReason is not null || !ReferenceEquals(offer, _offer) || _manager is null || _update is null)
            throw new InvalidOperationException("Check for updates again before installing.");
    }
}
