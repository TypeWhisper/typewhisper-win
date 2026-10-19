using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>How far an administrator lets the app handle its own updates (<c>UpdateMode</c>).</summary>
public enum AppUpdateMode
{
    /// <summary>Enterprise deployment owns updates: no checks, downloads or installations.</summary>
    External = 0,
    /// <summary>The app may check and announce versions but never installs them.</summary>
    CheckOnly = 1,
    /// <summary>Users may install explicitly; nothing installs unattended.</summary>
    UserInitiated = 2
}

/// <summary>The update part of a policy snapshot as typed values; absent values mean unmanaged behavior.</summary>
/// <param name="Mode">The enforced update mode, or null.</param>
/// <param name="EnforcedChannel">A channel users cannot leave, or null.</param>
/// <param name="RecommendedChannel">An administrator default for profiles without an explicit choice, or null.</param>
/// <param name="AllowedChannels">The channels updates may use; null is unrestricted, empty blocks updates.</param>
/// <param name="Blocked">True when the source is unreadable or an enforced update key is invalid.</param>
public sealed record ManagedUpdatePolicy(
    AppUpdateMode? Mode,
    AppUpdateChannel? EnforcedChannel,
    AppUpdateChannel? RecommendedChannel,
    IReadOnlySet<AppUpdateChannel>? AllowedChannels,
    bool Blocked)
{
    /// <summary>No policy: the release behavior.</summary>
    public static ManagedUpdatePolicy None { get; } = new(null, null, null, null, false);

    /// <summary>
    /// Reads <c>UpdateMode</c>, <c>UpdateChannel</c> and <c>AllowedUpdateChannels</c> from a snapshot. A machine-wide
    /// installation is updated by the organization's deployment, so it behaves as <see cref="AppUpdateMode.External"/>
    /// unless policy sets another mode.
    /// </summary>
    public static ManagedUpdatePolicy From(ManagedPolicySnapshot snapshot, bool machineWide = false)
    {
        var blocked = snapshot.IsBlocked(ManagedPolicyKeys.UpdateMode) || snapshot.IsBlocked(ManagedPolicyKeys.UpdateChannel)
            || snapshot.IsBlocked(ManagedPolicyKeys.AllowedUpdateChannels);
        var mode = snapshot.Enforced(ManagedPolicyKeys.UpdateMode) is { } enforcedMode ? (AppUpdateMode?)enforcedMode.Number
            : machineWide ? AppUpdateMode.External : null;
        var enforcedChannel = snapshot.Enforced(ManagedPolicyKeys.UpdateChannel) is { } channel ? (AppUpdateChannel?)channel.Number : null;
        var recommendedChannel = snapshot.Recommended(ManagedPolicyKeys.UpdateChannel) is { } recommended ? (AppUpdateChannel?)recommended.Number : null;
        HashSet<AppUpdateChannel>? allowed = null;
        if (snapshot.Enforced(ManagedPolicyKeys.AllowedUpdateChannels) is { Items: { } items })
        {
            allowed = [];
            foreach (var item in items)
                if (TryParseChannel(item, out var parsed)) allowed.Add(parsed);
        }
        return new(mode, enforcedChannel, recommendedChannel, allowed, blocked);
    }

    /// <summary>Maps the documented list names <c>stable</c>, <c>daily</c> and <c>release-candidate</c>.</summary>
    public static bool TryParseChannel(string name, out AppUpdateChannel channel)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "stable": channel = AppUpdateChannel.Stable; return true;
            case "daily": channel = AppUpdateChannel.Daily; return true;
            case "release-candidate": channel = AppUpdateChannel.ReleaseCandidate; return true;
            default: channel = default; return false;
        }
    }

    /// <summary>Whether any update policy applies.</summary>
    public bool IsManaged => Mode is not null || EnforcedChannel is not null || RecommendedChannel is not null || AllowedChannels is not null || Blocked;

    /// <summary>Whether users may change the channel.</summary>
    public bool ChannelLocked => EnforcedChannel is not null || Blocked;

    /// <summary>Whether a channel is inside the allowlist.</summary>
    public bool ChannelAllowed(AppUpdateChannel channel) => AllowedChannels is null || AllowedChannels.Contains(channel);

    /// <summary>Enforced channel, then the user's explicit choice, then the recommendation, then the installed track.</summary>
    public AppUpdateChannel Resolve(AppUpdateChannel? explicitChoice, AppUpdateChannel installed) =>
        ManagedSettingResolver.Resolve(installed,
            explicitChoice is { } choice ? new(choice) : null,
            RecommendedChannel is { } recommended ? new(recommended) : null,
            EnforcedChannel is { } enforced ? new(enforced) : null).Value;

    /// <summary>Why update checks on <paramref name="channel"/> are refused, or null when they may run.</summary>
    public string? CheckRefusal(AppUpdateChannel channel)
    {
        if (Blocked) return Loc.T("Your organization's update policy could not be read. App updates wait until it is readable.");
        if (Mode == AppUpdateMode.External) return Loc.T("App updates are managed by your organization's deployment.");
        if (ChannelAllowed(channel)) return null;
        if (EnforcedChannel is not null) return Loc.T("The update channel enforced by your organization is not among its allowed channels. Contact your administrator.");
        return AllowedChannels!.Count == 0
            ? Loc.T("App updates are turned off by your organization.")
            : Loc.T("The selected update channel is not allowed by your organization. Choose an allowed channel.");
    }

    /// <summary>Why installing from <paramref name="channel"/> is refused, or null when the user may install.</summary>
    public string? InstallRefusal(AppUpdateChannel channel) =>
        CheckRefusal(channel) ?? (Mode == AppUpdateMode.CheckOnly
            ? Loc.T("Installation is managed by your organization. TypeWhisper only checks for updates.") : null);
}
