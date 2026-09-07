using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    internal Func<PersistedProfileBackup, PersistedProfileBackupPreview, Task>? RestoreProfile { get; set; }
    private bool _profileRestoreClosing;
    private Task? _profileUiDrain;

    internal Task DrainBeforeProfileExitAsync() => ShutdownCoreAsync();

    internal void FreezeForProfileRestore()
    {
        _profileRestoreClosing = true;
        _closing = true;
        _hotkeyRegistration?.Dispose();
        _dictationHotkey?.Dispose();
        _cancelProcessingHotkey?.Dispose();
        _dictationInput?.Dispose();
        _dictation.RequestCancel();
        // Close admission immediately, including when recorder saving will need a retry.
        // These drains can wait for the recorder's reservation without blocking this call.
        _profileUiDrain = Task.WhenAll(HistoryView.ShutdownAsync(), WorkflowsView.ShutdownAsync(),
            DrainRecoveryViewsAsync(),
            DrainReviewWindowsAsync(),
            _lexicon?.ShutdownAsync() ?? Task.CompletedTask,
            _fileTranscription?.ShutdownAsync() ?? Task.CompletedTask,
            _dictation.HistoryRetention.CloseAndDrainAsync(),
            _dictationInput?.Completion ?? Task.CompletedTask);
        _settingsWindow?.Close();
        _overlay?.Close();
        _liveOverlay?.HidePreview();
        if (Content is Microsoft.UI.Xaml.UIElement content) content.IsHitTestVisible = false;
        AppWindow.Hide();
    }
}
