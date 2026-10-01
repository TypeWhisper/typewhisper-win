using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private ActionShortcut? _cancelProcessingShortcut;
    private ActionShortcut? _recentTranscriptionsShortcut;
    private ActionShortcut? _copyLastShortcut;
    private ActionShortcut? _pasteLastShortcut;
    private ActionShortcut? _readLastShortcut;
    private ActionShortcut? _workflowPaletteShortcut;
    private ActionShortcut? _recorderShortcut;

    // The chords each global shortcut has registered right now; unregistered shortcuts have none.
    private string RegisteredShortcut(string key) => key switch
    {
        GlobalShortcuts.MainDictation => _dictationHotkey?.Value ?? "",
        GlobalShortcuts.CancelProcessing => _cancelProcessingShortcut?.Value ?? "",
        GlobalShortcuts.Recorder => _recorderShortcut?.Value ?? "",
        GlobalShortcuts.WorkflowPalette => _workflowPaletteShortcut?.Value ?? "",
        GlobalShortcuts.RecentTranscriptions => _recentTranscriptionsShortcut?.Value ?? "",
        GlobalShortcuts.ReadLastTranscription => _readLastShortcut?.Value ?? "",
        GlobalShortcuts.CopyLastTranscription => _copyLastShortcut?.Value ?? "",
        GlobalShortcuts.PasteLastTranscription => _pasteLastShortcut?.Value ?? "",
        GlobalShortcuts.Workflows => _workflowShortcuts?.ActiveValue ?? "",
        _ => _recordingShortcuts.TryGetValue(key, out var recording) ? recording.Registration.Value : ""
    };

    // Every shortcut assignment, including saved values loaded at startup, goes through this one check.
    private string? ShortcutConflict(string key, string value) =>
        GlobalShortcuts.FindConflict(key, WorkflowShortcutCatalog.Canonical(value), RegisteredShortcut);

    private ActionShortcut? InitializeActionShortcut(string key, Action callback, int idBase, string fileName, string displayName, string name)
    {
        if (_closing || _profileRestoreClosing) return null;
        var shortcut = new ActionShortcut(this, key, callback, idBase, fileName, displayName, name, ShortcutConflict);
        var error = shortcut.Initialize();
        _settingsValues[key] = shortcut.Value;
        if (error is not null) ShowActivationNotice(error);
        return shortcut;
    }

    // name starts "… shortcuts are unavailable" while startup has not registered the shortcut yet.
    private string? ChangeActionShortcut(ActionShortcut? shortcut, string name, string value)
    {
        if (_closing || _profileRestoreClosing) return Loc.T("The app is shutting down.");
        if (shortcut is null) return Loc.T("{0} shortcuts are unavailable. Wait for startup to finish or restart.", name);
        var error = shortcut.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues[shortcut.Key] = shortcut.Value;
        return error;
    }

    // Startup, recording, provider changes and running workflows keep shortcut actions from interfering.
    private bool ShortcutActionBusy => _dictationInitialization is not { IsCompleted: true } || !_dictation.CanChangeProvider
        || _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false };

    // Dictation and workflow shortcuts have their own lifetimes; every other global shortcut stops here.
    private void DisposeShortcutRegistrations()
    {
        _cancelProcessingShortcut?.Dispose();
        _recentTranscriptionsShortcut?.Dispose();
        _workflowPaletteShortcut?.Dispose();
        DisposeRecordingShortcuts();
        _recorderShortcut?.Dispose();
        _copyLastShortcut?.Dispose();
        _pasteLastShortcut?.Dispose();
        _foregroundHistory?.Dispose();
        _readLastShortcut?.Dispose();
    }
}
