using System.Text.Json;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Compatible WinUI release tracks; none use legacy WPF package feeds.</summary>
public enum AppUpdateChannel
{
    /// <summary>Final releases.</summary>
    Stable,
    /// <summary>Daily development builds.</summary>
    Daily,
    /// <summary>Prerelease candidates.</summary>
    ReleaseCandidate
}

/// <summary>Persists an explicit channel, defaulting to the installed version's track.</summary>
public sealed class AppUpdatePreferences
{
    private readonly string _path;
    /// <summary>The loaded or last saved update track.</summary>
    public AppUpdateChannel Channel { get; private set; }
    /// <summary>The track the installed build was published on, inferred from its version.</summary>
    public AppUpdateChannel InstalledChannel { get; }
    /// <summary>A readable persistence failure.</summary>
    public string? Error { get; private set; }
    /// <summary>Loads an explicit preference or infers the installed track without writing.</summary>
    public AppUpdatePreferences(string path, string version)
    {
        _path = path;
        InstalledChannel = version.Contains("-daily.", StringComparison.OrdinalIgnoreCase) ? AppUpdateChannel.Daily
            : version.Contains("-rc", StringComparison.OrdinalIgnoreCase) ? AppUpdateChannel.ReleaseCandidate : AppUpdateChannel.Stable;
        Channel = InstalledChannel;
        try
        {
            if (!File.Exists(path)) return;
            var value = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
            if (!Enum.TryParse<AppUpdateChannel>(value, out var channel) || !Enum.IsDefined(channel)) throw new JsonException();
            Channel = channel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = Loc.T("The saved update channel could not be loaded."); }
    }
    /// <summary>Atomically saves a valid track; a failed write preserves the current selection.</summary>
    public bool Save(AppUpdateChannel channel)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(channel.ToString()));
            Channel = channel; Error = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = Loc.T("The update channel could not be saved."); return false; }
    }
    /// <summary>Resolves a compatible WinUI feed for a supported Windows architecture.</summary>
    public static string Feed(AppUpdateChannel channel, string architecture) =>
        architecture is not ("win-x64" or "win-arm64") ? throw new ArgumentException("Unsupported architecture.", nameof(architecture))
        : architecture + "-winui-" + (channel switch
        {
            AppUpdateChannel.Stable => "stable", AppUpdateChannel.Daily => "daily",
            AppUpdateChannel.ReleaseCandidate => "rc", _ => throw new ArgumentOutOfRangeException(nameof(channel))
        });
}

/// <summary>
/// Remembers how the user answered the automatic update notice, as Sparkle does on macOS: "Later" waits a day,
/// "Skip this version" stays silent about that version only. Manual checks ignore both.
/// </summary>
public sealed class AppUpdateReminder
{
    /// <summary>How long "Later" postpones the notice, and how often TypeWhisper checks in the background.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private readonly string _path;
    private State _state = new(null, null);
    /// <summary>Loads earlier answers; an unreadable file only means the notice may appear again.</summary>
    public AppUpdateReminder(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? _state; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    /// <summary>The version the user chose to skip.</summary>
    public string? SkippedVersion => _state.SkippedVersion;
    /// <summary>Whether an automatic check should announce <paramref name="version"/> at <paramref name="now"/>.</summary>
    public bool ShouldNotify(string version, DateTimeOffset now) =>
        !string.Equals(version, _state.SkippedVersion, StringComparison.OrdinalIgnoreCase) && !(now < _state.RemindAfter);
    /// <summary>Postpones the notice for a day.</summary>
    public void Later(DateTimeOffset now) => Save(_state with { RemindAfter = now + Interval });
    /// <summary>Stays silent about <paramref name="version"/>, also if the feed offers it again after another one.</summary>
    public void Skip(string version) => Save(new(version, null));
    // A failed write keeps the answer for this session.
    private void Save(State state)
    {
        _state = state;
        try { AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(state)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed record State(string? SkippedVersion, DateTimeOffset? RemindAfter);
}

/// <summary>An available version and whether installing it would downgrade the app.</summary>
public sealed record AppUpdateOffer(string Version, bool IsDowngrade);
/// <summary>Distinguishes an unpublished track from an up-to-date installation.</summary>
public sealed record AppUpdateCheck(bool ChannelPublished, AppUpdateOffer? Offer);
/// <summary>Platform update operations; checks and downloads do not shut down the application.</summary>
public interface IAppUpdateBackend
{
    /// <summary>Explains why this host cannot install application updates.</summary>
    string? UnavailableReason { get; }
    /// <summary>Checks only the selected compatible track. Older versions are offered only while <paramref name="allowDowngrade"/> is set.</summary>
    Task<AppUpdateCheck> CheckAsync(AppUpdateChannel channel, bool allowDowngrade);
    /// <summary>Downloads and verifies the previously offered package.</summary>
    Task DownloadAsync(AppUpdateOffer offer);
    /// <summary>Applies the verified package after the host has drained its work.</summary>
    void Apply(AppUpdateOffer offer);
}

/// <summary>Serializes channel changes, checks and explicit installation; old offers cannot survive a channel switch.</summary>
public sealed class AppUpdateController(AppUpdatePreferences preferences, IAppUpdateBackend backend,
    Func<Action, Task<string?>> shutdownAndApply)
{
    /// <summary>The persisted update selection.</summary>
    public AppUpdatePreferences Preferences => preferences;
    /// <summary>Whether a check or installation owns the controller.</summary>
    public bool Busy { get; private set; }
    /// <summary>The available package for the current selection.</summary>
    public AppUpdateOffer? Offer { get; private set; }
    /// <summary>Human-readable operation progress or failure.</summary>
    public string Status { get; private set; } = preferences.Error ?? backend.UnavailableReason ?? Loc.T("Choose a channel and check for updates.");
    /// <summary>Whether this host can begin another check.</summary>
    public bool CanCheck => !Busy && backend.UnavailableReason is null;
    /// <summary>
    /// Downgrades are offered only while the selected channel differs from the installed build's track,
    /// that is after an explicit channel switch. A feed that moves backwards on the installed track never
    /// offers an older version.
    /// </summary>
    public bool AllowsDowngrade => preferences.Channel != preferences.InstalledChannel;
    /// <summary>Notifies UI subscribers after state changes.</summary>
    public event Action? Changed;
    /// <summary>Saves a selection while idle and discards any earlier package offer.</summary>
    public bool Select(AppUpdateChannel channel)
    {
        if (Busy) return false;
        if (!preferences.Save(channel)) { Status = preferences.Error!; Changed?.Invoke(); return false; }
        Offer = null; Status = backend.UnavailableReason ?? Loc.T("Channel saved. Check for updates to see available versions.");
        Changed?.Invoke(); return true;
    }
    /// <summary>Checks the selected track without allowing concurrent selection changes.</summary>
    public async Task CheckAsync()
    {
        if (!CanCheck) return;
        Busy = true; Offer = null; Status = Loc.T("Checking for updates…"); Changed?.Invoke();
        try
        {
            var result = await backend.CheckAsync(preferences.Channel, AllowsDowngrade);
            Offer = result.Offer;
            Status = !result.ChannelPublished ? Loc.T("No compatible WinUI release is available on this channel yet.")
                : Offer is null ? Loc.T("You are up to date on this channel.")
                : Offer.IsDowngrade ? Loc.T("Version {0} is older than your installed version. Installing it switches to the selected channel.", Offer.Version)
                : Loc.T("Version {0} is available.", Offer.Version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.T("Update check failed: {0}", ex.Message); }
        finally { Busy = false; Changed?.Invoke(); }
    }
    /// <summary>Downloads first, then requests orderly host shutdown and explicit installation.</summary>
    public async Task InstallAsync()
    {
        if (Busy || Offer is not { } offer || backend.UnavailableReason is not null) return;
        Busy = true; Status = Loc.T("Downloading update…"); Changed?.Invoke();
        try
        {
            await backend.DownloadAsync(offer);
            Status = Loc.T("Finishing work and restarting…"); Changed?.Invoke();
            Status = await shutdownAndApply(() => backend.Apply(offer)) ?? Loc.T("Restart requested.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.T("Update installation failed: {0}", ex.Message); }
        finally { Busy = false; Changed?.Invoke(); }
    }
}
