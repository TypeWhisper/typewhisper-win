using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class ProcessingCancelHotkeyRegistration : IDisposable
{
    private readonly PrototypeHotkeyRegistration _native;
    private readonly ProcessingCancelShortcut _settings;
    private readonly Func<string> _launcher;
    private readonly Func<string> _dictation;
    private bool _disposed;
    internal string Value => _settings.Value;
    internal string? Error => _settings.Error;
    internal ProcessingCancelHotkeyRegistration(Microsoft.UI.Xaml.Window window, Func<bool> canCancel,
        Action requestCancel, Func<string> launcherShortcuts, Func<string> dictationShortcuts)
    {
        _launcher = launcherShortcuts; _dictation = dictationShortcuts;
        _native = new(window, () =>
        {
            if (!_disposed) ProcessingCancelShortcut.Invoke(canCancel(), PrototypeShortcutRecorder.AnyEditing, requestCancel);
        }, 0x7500);
        _settings = new(WinUIProfile.DataPath("cancel-processing-hotkeys.txt"), new Backend(_native), Validate);
    }
    internal string? Initialize() => _disposed ? "Cancel shortcuts are unavailable during shutdown." : _settings.Initialize();
    internal string? TryChange(string value) => _disposed ? "Cancel shortcuts are unavailable during shutdown." : _settings.Save(Canonical(value));
    internal string? ConflictWithLauncher(string value) => ProcessingCancelShortcut.Conflicts(Value, Canonical(value), false)
        ? "Already used by Cancel processing. Change that shortcut first." : null;
    internal string? ConflictWithDictation(string value) => ProcessingCancelShortcut.Conflicts(Value, Canonical(value), true)
        ? "This dictation shortcut overlaps Cancel processing. Change the cancel shortcut first." : null;
    private string? Validate(string value)
    {
        if (value != Canonical(value)) return "Assign the cancel shortcut again using the shortcut editor.";
        foreach (var chord in PrototypeShortcutRules.Split(value))
            if (PrototypeShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, Canonical(_launcher()), false)) return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, Canonical(_dictation()), true)) return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        return null;
    }
    private static string Canonical(string value) => string.Join(",", PrototypeShortcutRules.Split(value).Select(PrototypeShortcutRules.Normalize).Distinct());
    public void Dispose() { if (_disposed) return; _disposed = true; _native.Dispose(); }
    private sealed class Backend(PrototypeHotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
