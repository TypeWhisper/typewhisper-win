using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// A global shortcut for one app action: its native registration and the choice saved in the profile.
internal sealed class ActionShortcut : IDisposable
{
    private readonly HotkeyRegistration _native;
    private readonly PersistedShortcut _settings;
    private readonly string _displayName;
    private readonly string _name;
    private readonly Func<string, string, string?> _conflict;
    private bool _disposed;
    internal string Key { get; }
    internal string Value => _settings.Value;
    internal string? Error => _settings.Error;

    // name completes "Assign the … shortcut again"; conflict checks a value against every other global shortcut.
    internal ActionShortcut(Microsoft.UI.Xaml.Window window, string key, Action callback, int idBase, string fileName,
        string displayName, string name, Func<string, string, string?> conflict)
    {
        Key = key; _displayName = displayName; _name = name; _conflict = conflict;
        _native = new(window, () => { if (!_disposed) callback(); }, idBase);
        _settings = new(WinUIProfile.DataPath(fileName), _native, Validate, displayName);
    }
    internal string? Initialize() => _disposed ? Unavailable : _settings.Initialize();
    internal string? Save(string value) => _disposed ? Unavailable : _settings.Save(value);
    private string Unavailable => _displayName + " are unavailable during shutdown.";
    private string? Validate(string value)
    {
        // Saved values are canonical; anything else was edited outside the app.
        if (value != WorkflowShortcutCatalog.Canonical(value)) return $"Assign the {_name} shortcut again using the shortcut editor.";
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) return error;
        return _conflict(Key, value);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _native.Dispose(); }
}
