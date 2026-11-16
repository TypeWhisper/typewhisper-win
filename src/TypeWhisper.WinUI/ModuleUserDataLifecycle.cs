using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// Module folders live in the profile, so "Export all data" and "Delete all data" cover them with the host's rules.
// The erase work modules register runs before the host deletes the profile.
internal sealed class ModuleUserDataLifecycle : IUserDataLifecycle
{
    internal const string FolderName = "Modules";
    private const int MaximumIdLength = 128;

    // Names Windows reserves for devices, also with an extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // How long one module's erase work may hold up "Delete all data".
    internal static readonly TimeSpan DefaultEraseTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root;
    private readonly Action<string, Exception> _report;
    private readonly TimeSpan _eraseTimeout;
    private readonly List<ModuleUserData> _registered = [];
    private volatile bool _erasing;

    // root: the folder that holds one subfolder per module.
    internal ModuleUserDataLifecycle(string root, Action<string, Exception> report, TimeSpan? eraseTimeout = null)
    {
        _root = root;
        _report = report;
        _eraseTimeout = eraseTimeout ?? DefaultEraseTimeout;
    }

    public string DataFolder(string moduleId)
    {
        RequireValidId(moduleId, nameof(moduleId));
        // Erase work that outlives its timeout must not recreate a folder the deletion has already removed.
        if (_erasing) throw new InvalidOperationException("All data is being deleted.");
        var folder = Path.Join(_root, moduleId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Register(ModuleUserData userData)
    {
        ArgumentNullException.ThrowIfNull(userData);
        RequireValidId(userData.Id, nameof(userData));
        ArgumentNullException.ThrowIfNull(userData.Erase, nameof(userData));
        lock (_registered)
        {
            var index = _registered.FindIndex(registered => registered.Id == userData.Id);
            if (index >= 0) _registered[index] = userData;
            else _registered.Add(userData);
        }
    }

    // Runs every registered erase in order of registration on the calling thread. A failure or a timeout is reported
    // and the others still run, because the host deletes the data afterwards either way.
    internal async Task EraseAllAsync()
    {
        _erasing = true;
        ModuleUserData[] registered;
        lock (_registered) registered = [.. _registered];
        foreach (var userData in registered)
        {
            try { await (userData.Erase() ?? Task.CompletedTask).WaitAsync(_eraseTimeout); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _report($"modules.user-data.erase-failed module={userData.Id}", ex); }
        }
    }

    // ASCII letters, digits, dots, hyphens and underscores; never a path step, a trailing dot Windows would drop, or a device name.
    internal static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= MaximumIdLength } && id is not ("." or "..") && !id.EndsWith('.')
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && !ReservedNames.Contains(id.Split('.')[0]);

    private static void RequireValidId(string? id, string parameter)
    {
        if (!IsValidId(id)) throw new ArgumentException("A module identifier uses ASCII letters, digits, dots, hyphens and underscores.", parameter);
    }
}
