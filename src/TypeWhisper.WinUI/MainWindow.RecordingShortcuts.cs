using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, (DictationHotkeyRegistration Registration, PersistedShortcut Settings)> _recordingShortcuts = [];

    private void DispatchRecordingShortcut(HybridHotkeyAction action)
    {
        AppDiagnostics.Write("shortcut." + action.ToString().ToLowerInvariant());
        if (action is HybridHotkeyAction.Start or HybridHotkeyAction.Toggle)
            _dictation.ShowLoadingForDictationAttempt();
        // A modifier chord such as Ctrl+Shift+Left emits Cancel after a rejected Start. Only
        // interrupt capture that this gesture owns, never processing, files or model work.
        if (action == HybridHotkeyAction.Cancel && _dictationInput?.IsRecordingOrStarting == true) _dictation.RequestCancel();
        _ = _dictationInput?.SubmitAsync(action switch
        {
            HybridHotkeyAction.Start => TypeWhisper.Presentation.DictationInputAction.Start,
            HybridHotkeyAction.Stop => TypeWhisper.Presentation.DictationInputAction.Stop,
            HybridHotkeyAction.Cancel => TypeWhisper.Presentation.DictationInputAction.Cancel,
            _ => TypeWhisper.Presentation.DictationInputAction.Toggle
        });

    }

    private void InitializeRecordingShortcuts()
    {
        if (_closing || _profileRestoreClosing) return;
        var id = 0x8200;
        foreach (var (key, mode) in new[] { (GlobalShortcuts.PushToTalk, RecordingMode.Hold), (GlobalShortcuts.ToggleOnly, RecordingMode.Toggle), (GlobalShortcuts.HoldOnly, RecordingMode.Hold) })
        {
            var registration = new DictationHotkeyRegistration(this, DispatchRecordingShortcut,
                () => _dictationInput?.IsRecordingOrStarting == true, () => mode, () => DictationHotkeysPaused, id);
            id += 0x200;
            var settings = new PersistedShortcut(WinUIProfile.DataPath(key + ".txt"),
                registration, value => ValidateRecordingShortcut(key, value), Loc.T("Recording shortcuts"));
            _recordingShortcuts.Add(key, (registration, settings));
            var error = settings.Initialize();
            _settingsValues[key] = settings.Value;
            if (error is not null) ShowActivationNotice(error);
        }
    }

    private string? ValidateRecordingShortcut(string key, string value)
    {
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, true) is { } error) return error;
        return ShortcutConflict(key, value);
    }

    private string? ChangeRecordingShortcut(string key, string value)
    {
        if (_closing || _profileRestoreClosing) return Loc.T("The app is shutting down.");
        if (_dictationInput?.IsRecordingOrStarting == true) return Loc.T("Finish recording before changing its shortcut.");
        if (!_recordingShortcuts.TryGetValue(key, out var entry)) return Loc.T("Wait for shortcut initialization to finish.");
        var error = entry.Settings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues[key] = entry.Settings.Value;
        return error;
    }

    private void DisposeRecordingShortcuts()
    {
        foreach (var entry in _recordingShortcuts.Values) entry.Registration.Dispose();
        _recordingShortcuts.Clear();
    }
}
