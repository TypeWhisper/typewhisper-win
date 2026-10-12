using System.Reflection;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;
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
#if TYPEWHISPER_STORE
            return Loc.T("App updates are managed by Microsoft Store.");
#elif DEBUG
            return Loc.T("Update channels are saved here. Update checks and installation are available in the installed app.");
#else
            var locator = Velopack.Locators.VelopackLocator.Current;
            var installation = ApplicationInstallation.Resolve(locator.AppId);
            return installation is not null && locator.CurrentlyInstalledVersion is not null && !string.IsNullOrWhiteSpace(locator.RootAppDir)
                && string.Equals(Environment.ProcessPath, Path.Combine(locator.RootAppDir!, "current", installation.Executable), StringComparison.OrdinalIgnoreCase)
                ? null : Loc.T("Install TypeWhisper using its installer to check for app updates.");
#endif
        }
    }
    // Downgrades stay off unless the controller reports an explicit channel switch; an older
    // package on the installed track is then reported as "up to date" instead of being offered.
    public async Task<AppUpdateCheck> CheckAsync(AppUpdateChannel channel, bool allowDowngrade)
    {
        _update = null; _offer = null; _manager = null;
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var installation = ApplicationInstallation.Resolve(Velopack.Locators.VelopackLocator.Current.AppId)!;
        if (!installation.Supports(channel))
            throw new InvalidOperationException(Loc.T("This installation receives Daily builds only. Install TypeWhisper from the download page to use Stable or Release Candidate."));
        var feed = installation.Feed(channel);
        var source = new AppReleaseGithubSource("https://github.com/TypeWhisper/typewhisper-win", feed, channel != AppUpdateChannel.Stable);
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = feed, AllowVersionDowngrade = allowDowngrade });
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return new(source.HasMatchingRelease != false, null);
            var target = update.TargetFullRelease;
            if (!installation.Accepts(target.PackageId, target.Version.Major, target.Version.Minor))
            {
                // The original installation shares its feeds with 1.0. Until a 1.1 release reaches the
                // channel, its newest entry is a 1.0 package, which counts as no release rather than an error.
                if (target.PackageId == installation.PackageId) return new(false, null);
                throw new InvalidDataException(Loc.T("The release uses an incompatible installation identity."));
            }
            _manager = manager; _update = update;
            _offer = new(target.Version.ToString(), manager.CurrentVersion is { } current && target.Version < current);
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
            throw new InvalidOperationException(Loc.T("Check for updates again before installing."));
    }
}
