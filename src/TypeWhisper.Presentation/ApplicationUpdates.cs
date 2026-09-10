using System.Text.Json;

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
    /// <summary>A readable persistence failure.</summary>
    public string? Error { get; private set; }
    /// <summary>Loads an explicit preference or infers the installed track without writing.</summary>
    public AppUpdatePreferences(string path, string version)
    {
        _path = path;
        Channel = version.Contains("-daily.", StringComparison.OrdinalIgnoreCase) ? AppUpdateChannel.Daily
            : version.Contains("-rc", StringComparison.OrdinalIgnoreCase) ? AppUpdateChannel.ReleaseCandidate : AppUpdateChannel.Stable;
        try
        {
            if (!File.Exists(path)) return;
            var value = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
            if (!Enum.TryParse<AppUpdateChannel>(value, out var channel) || !Enum.IsDefined(channel)) throw new JsonException();
            Channel = channel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = "The saved update channel could not be loaded."; }
    }
    /// <summary>Atomically saves a valid track; a failed write preserves the current selection.</summary>
    public bool Save(AppUpdateChannel channel)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(channel.ToString()));
            File.Move(temporary, _path, true);
            Channel = channel; Error = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "The update channel could not be saved."; return false; }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
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

/// <summary>An available version and whether installing it would downgrade the app.</summary>
public sealed record AppUpdateOffer(string Version, bool IsDowngrade);
/// <summary>Distinguishes an unpublished track from an up-to-date installation.</summary>
public sealed record AppUpdateCheck(bool ChannelPublished, AppUpdateOffer? Offer);
/// <summary>Platform update operations; checks and downloads do not shut down the application.</summary>
public interface IAppUpdateBackend
{
    /// <summary>Explains why this host cannot install application updates.</summary>
    string? UnavailableReason { get; }
    /// <summary>Checks only the selected compatible track.</summary>
    Task<AppUpdateCheck> CheckAsync(AppUpdateChannel channel);
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
    public string Status { get; private set; } = preferences.Error ?? backend.UnavailableReason ?? "Choose a channel and check for updates.";
    /// <summary>Whether this host can begin another check.</summary>
    public bool CanCheck => !Busy && backend.UnavailableReason is null;
    /// <summary>Notifies UI subscribers after state changes.</summary>
    public event Action? Changed;
    /// <summary>Saves a selection while idle and discards any earlier package offer.</summary>
    public bool Select(AppUpdateChannel channel)
    {
        if (Busy) return false;
        if (!preferences.Save(channel)) { Status = preferences.Error!; Changed?.Invoke(); return false; }
        Offer = null; Status = backend.UnavailableReason ?? "Channel saved. Check for updates to see available versions.";
        Changed?.Invoke(); return true;
    }
    /// <summary>Checks the selected track without allowing concurrent selection changes.</summary>
    public async Task CheckAsync()
    {
        if (!CanCheck) return;
        Busy = true; Offer = null; Status = "Checking for updates…"; Changed?.Invoke();
        try
        {
            var result = await backend.CheckAsync(preferences.Channel);
            Offer = result.Offer;
            Status = !result.ChannelPublished ? "No compatible WinUI release is available on this channel yet."
                : Offer is null ? "You are up to date on this channel."
                : Offer.IsDowngrade ? $"Version {Offer.Version} is older than your installed version. Installing it switches to the selected channel."
                : $"Version {Offer.Version} is available.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "Update check failed: " + ex.Message; }
        finally { Busy = false; Changed?.Invoke(); }
    }
    /// <summary>Downloads first, then requests orderly host shutdown and explicit installation.</summary>
    public async Task InstallAsync()
    {
        if (Busy || Offer is not { } offer || backend.UnavailableReason is not null) return;
        Busy = true; Status = "Downloading update…"; Changed?.Invoke();
        try
        {
            await backend.DownloadAsync(offer);
            Status = "Finishing work and restarting…"; Changed?.Invoke();
            Status = await shutdownAndApply(() => backend.Apply(offer)) ?? "Restart requested.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "Update installation failed: " + ex.Message; }
        finally { Busy = false; Changed?.Invoke(); }
    }
}
