using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, (DictationHotkeyRegistration Registration, ProcessingCancelShortcut Settings)> _recordingShortcuts = [];

    private void DispatchRecordingShortcut(HybridHotkeyAction action)
    {
        if (action is HybridHotkeyAction.Start or HybridHotkeyAction.Toggle)
            _dictation.ShowLoadingForDictationAttempt();
        if (action == HybridHotkeyAction.Cancel) _dictation.RequestCancel();
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
        foreach (var (key, mode) in new[] { ("PushToTalkHotkey", RecordingMode.Hold), ("ToggleOnlyHotkeys", RecordingMode.Toggle), ("HoldOnlyHotkeys", RecordingMode.Hold) })
        {
            var registration = new DictationHotkeyRegistration(this, DispatchRecordingShortcut,
                () => _dictationInput?.IsRecordingOrStarting == true, () => mode, () => DictationHotkeysPaused, id);
            id += 0x200;
            var settings = new ProcessingCancelShortcut(WinUIProfile.DataPath(key + ".txt"),
                new RecordingShortcutBackend(registration), value => ValidateRecordingShortcut(key, value), "Recording shortcuts");
            _recordingShortcuts.Add(key, (registration, settings));
            var error = settings.Initialize();
            _settingsValues[key] = settings.Value;
            if (error is not null) ShowActivationNotice(error);
        }
    }

    private string? RecordingShortcutConflict(string value, bool modifierOnly = false, string? except = null)
    {
        foreach (var (key, entry) in _recordingShortcuts)
            if (key != except && RecordingShortcutConflicts.Overlap(value, modifierOnly, entry.Registration.Value, true))
                return "Already used by another recording shortcut. Choose a different combination.";
        return null;
    }

    private string? ValidateRecordingShortcut(string key, string value)
    {
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, true) is { } error) return error;
        if (RecordingShortcutConflicts.Overlap(value, true, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "Already used by Main dictation. Choose a different combination.";
        if (RecordingShortcutConflicts.Overlap(value, true, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (RecordingShortcutConflicts.Overlap(value, true, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return RecordingShortcutConflict(value, true, key) ?? RecorderShortcutConflict(value, true)
            ?? HistoryShortcutConflict(value, true) ?? CopyLastShortcutConflict(value, true)
            ?? ReadLastShortcutConflict(value, true) ?? WorkflowPaletteShortcutConflict(value, true)
            ?? _workflowShortcuts?.Conflict(value, modifierOnly: true);
    }

    private string? ChangeRecordingShortcut(string key, string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_dictationInput?.IsRecordingOrStarting == true) return "Finish recording before changing its shortcut.";
        if (!_recordingShortcuts.TryGetValue(key, out var entry)) return "Wait for shortcut initialization to finish.";
        var error = entry.Settings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues[key] = entry.Settings.Value;
        return error;
    }

    private void DisposeRecordingShortcuts()
    {
        foreach (var entry in _recordingShortcuts.Values) entry.Registration.Dispose();
        _recordingShortcuts.Clear();
    }

    private sealed class RecordingShortcutBackend(DictationHotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
