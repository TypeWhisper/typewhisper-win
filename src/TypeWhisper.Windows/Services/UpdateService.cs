using System.Reflection;
using System.Runtime.InteropServices;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Windows.Services.Localization;
using Velopack;
using Velopack.Sources;

namespace TypeWhisper.Windows.Services;

public sealed class UpdateService
{
    internal const string StableChannelSetting = "stable";
    internal const string ReleaseCandidateChannelSetting = "release-candidate";
    internal const string DailyChannelSetting = "daily";

    private readonly TrayIconService _trayIcon;
    private readonly ISettingsService _settings;
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

    public ReleaseChannel Channel { get; private set; } = ReleaseChannel.Stable;

    public event EventHandler? UpdateAvailable;

    public UpdateService(TrayIconService trayIcon, ISettingsService settings)
    {
        _trayIcon = trayIcon;
        _settings = settings;
    }

    public void Initialize(ReleaseChannel? channel = null)
    {
        var resolvedChannel = channel ?? ResolveReleaseChannel(_settings.Current.UpdateChannel, CurrentVersion);
        Channel = resolvedChannel;
        _pendingUpdate = null;
        _updateManager = null;
        try
        {
            _updateManager = new UpdateManager(
                new GithubSource(TypeWhisperEnvironment.GithubRepoUrl, null, resolvedChannel != ReleaseChannel.Stable),
                new UpdateOptions { ExplicitChannel = GetVelopackChannel(RuntimeInformation.OSArchitecture, resolvedChannel) });
        }
        catch
        {
            // Update check is best-effort
        }
    }

    public void SwitchChannel(ReleaseChannel channel)
    {
        Initialize(channel);
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_updateManager is null) return;

        try
        {
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_pendingUpdate is not null)
            {
                _trayIcon.ShowBalloon(Loc.Instance["Update.BalloonTitle"],
                    Loc.Instance.GetString("Update.BalloonMessage", AvailableVersion ?? ""),
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
            _trayIcon.ShowBalloon(Loc.Instance["Update.BalloonFailedTitle"],
                Loc.Instance["Update.BalloonFailedMessage"]);
        }
    }

    internal static ReleaseChannel ResolveReleaseChannel(string? configuredChannel, string? installedVersion)
    {
        return ParseReleaseChannel(configuredChannel) ?? InferReleaseChannel(installedVersion);
    }

    internal static ReleaseChannel? ParseReleaseChannel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            StableChannelSetting => ReleaseChannel.Stable,
            ReleaseCandidateChannelSetting or "rc" => ReleaseChannel.ReleaseCandidate,
            DailyChannelSetting => ReleaseChannel.Daily,
            _ => null
        };
    }

    internal static string ToSettingsValue(ReleaseChannel channel)
    {
        return channel switch
        {
            ReleaseChannel.ReleaseCandidate => ReleaseCandidateChannelSetting,
            ReleaseChannel.Daily => DailyChannelSetting,
            _ => StableChannelSetting
        };
    }

    internal static ReleaseChannel InferReleaseChannel(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return ReleaseChannel.Stable;

        if (version.Contains("-daily.", StringComparison.OrdinalIgnoreCase))
            return ReleaseChannel.Daily;

        if (version.Contains("-rc", StringComparison.OrdinalIgnoreCase))
            return ReleaseChannel.ReleaseCandidate;

        return ReleaseChannel.Stable;
    }

    internal static string GetVelopackChannel(Architecture architecture, ReleaseChannel channel)
    {
        var arch = architecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        return channel switch
        {
            ReleaseChannel.ReleaseCandidate => $"{arch}-rc",
            ReleaseChannel.Daily => $"{arch}-daily",
            _ => arch
        };
    }
}

public enum ReleaseChannel
{
    Stable,
    ReleaseCandidate,
    Daily
}
