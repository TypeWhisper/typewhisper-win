using TypeWhisper.ModuleKit;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Module contributions that belong to the main window: hotkey actions, tray entries and panels at shutdown.
public sealed partial class MainWindow
{
    // By action identifier. Filled at startup after the host's own shortcuts, so a saved module chord that a host
    // shortcut already uses is refused and reported like any other conflict.
    private readonly Dictionary<string, ActionShortcut> _moduleShortcuts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GlobalShortcut> _moduleGlobalShortcuts = [];

    private static ModuleContributionCatalog ModuleCatalog => HostModules.ActiveCatalog;

    // Completes IModuleHost.WhenStarted on the UI thread.
    internal void CompleteModuleStartup() => _services.ModuleHost.CompleteStartup();

    // Builds the conflict list before any module shortcut registers, so Settings can check against all of them.
    private void PrepareModuleShortcuts() => _moduleGlobalShortcuts = ModuleCatalog.HotkeyActions
        .Select(action => new GlobalShortcut(ModuleHotkeys.SettingsKey(action.Id), GlobalShortcutRole.Action, action.Title)).ToArray();

    private static ModuleHotkeyAction? FindModuleHotkeyAction(string key) =>
        ModuleHotkeys.TryActionId(key, out var id) ? ModuleCatalog.HotkeyActions.FirstOrDefault(action => string.Equals(action.Id, id, StringComparison.OrdinalIgnoreCase)) : null;

    private void InitializeModuleShortcuts()
    {
        var index = 0;
        foreach (var action in ModuleCatalog.HotkeyActions)
        {
            var key = ModuleHotkeys.SettingsKey(action.Id);
            try
            {
                if (InitializeActionShortcut(key, () => RunModuleHotkey(action), ModuleHotkeys.IdBase(index++), ModuleHotkeys.FileName(action.Id),
                    Loc.T("{0} shortcuts", action.Title), action.Title) is { } shortcut)
                    _moduleShortcuts[action.Id] = shortcut;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A shortcut that cannot register leaves the others and the app working.
                AppDiagnostics.WriteFailure(ModuleCalls.FailureStage("hotkey-action.register", action.Id), ex);
                _settingsValues[key] = "";
            }
        }
    }

    private void RunModuleHotkey(ModuleHotkeyAction action)
    {
        if (_closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing) return;
        ModuleCalls.Run("hotkey-action", action.Id, action.Perform, AppDiagnostics.WriteFailure);
    }

    private void DisposeModuleShortcuts()
    {
        foreach (var shortcut in _moduleShortcuts.Values) shortcut.Dispose();
        _moduleShortcuts.Clear();
    }

    // The tray entries; each runs on the UI thread after the menu has closed.
    internal IReadOnlyList<TrayModuleItem> ModuleTrayItems() => ModuleCatalog.MenuItems.Select(item => new TrayModuleItem(item.Title, item.Glyph,
        () => item.HotkeyActionId is { } id ? ModuleShortcutDisplay(id) : null,
        () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing && !_profileRestoreClosing) ModuleCalls.Run("menu-item", item.Id, item.Perform, AppDiagnostics.WriteFailure);
        }))).ToArray();

    // The first chord bound to a module action, as the tray shows it, or null.
    private string? ModuleShortcutDisplay(string actionId) =>
        _moduleShortcuts.GetValueOrDefault(actionId)?.Value is { Length: > 0 } value && ShortcutRules.Split(value) is [var first, ..]
            ? ShortcutKeys.Display(first) : null;
}
