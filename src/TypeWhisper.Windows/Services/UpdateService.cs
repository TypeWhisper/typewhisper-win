using System.Reflection;
using System.Runtime.InteropServices;
using TypeWhisper.Core;
using Velopack;
using Velopack.Sources;

namespace TypeWhisper.Windows.Services;

public sealed class UpdateService
{
    private readonly TrayIconService _trayIcon;
    private UpdateManager? _updateManager;
    private UpdateInfo? _pendingUpdate;

    public bool IsUpdateAvailable => _pendingUpdate is not null;
    public string? AvailableVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();

    public string CurrentVersion
    {
        get
        {
            if (_updateManager is { IsInstalled: true, CurrentVersion: { } ver })
                return ver.ToString();

            var info = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrEmpty(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            return "dev";
        }
    }

    public event EventHandler? UpdateAvailable;

    public UpdateService(TrayIconService trayIcon)
    {
        _trayIcon = trayIcon;
    }

    public void Initialize()
    {
        try
        {
            var channel = RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "win-arm64" : "win-x64";
            _updateManager = new UpdateManager(
                new GithubSource(TypeWhisperEnvironment.GithubRepoUrl, null, false),
                new UpdateOptions { ExplicitChannel = channel });
        }
        catch
        {
            // Update check is best-effort
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_updateManager is null) return;

        try
        {
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_pendingUpdate is not null)
            {
                _trayIcon.ShowBalloon("Update verfügbar",
                    $"Version {AvailableVersion} ist verfügbar. Klicken Sie hier zum Aktualisieren.",
                    () => _ = DownloadAndApplyAsync());
                UpdateAvailable?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // Silent fail - update check is non-critical
        }
    }

    public async Task DownloadAndApplyAsync()
    {
        if (_updateManager is null || _pendingUpdate is null) return;

        try
        {
            await _updateManager.DownloadUpdatesAsync(_pendingUpdate);
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        catch
        {
            _trayIcon.ShowBalloon("Update fehlgeschlagen",
                "Das Update konnte nicht installiert werden. Bitte versuchen Sie es später erneut.");
        }
    }
}
