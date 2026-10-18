using Microsoft.UI.Dispatching;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private readonly WhatsNewSession _whatsNewSession;
    private WhatsNewWindow? _whatsNewWindow;

    private bool WhatsNewBusy => _closing || _profileRestoreClosing || ShortcutActionBusy
        || _dictation.HasActiveUserWork || WorkflowsView.IsBusy;

    private WhatsNewWindow CreateWhatsNewWindow()
    {
        var window = _whatsNewWindow = new WhatsNewWindow();
        window.Closed += (_, _) => { if (ReferenceEquals(_whatsNewWindow, window)) _whatsNewWindow = null; };
        return window;
    }

    private string? OpenWhatsNew()
    {
        try
        {
            if (!_whatsNewSession.TryShowManually(WhatsNewBusy, () =>
            {
                var window = _whatsNewWindow;
                if (window is null) { window = CreateWhatsNewWindow(); window.ShowWithoutTakingFocus(); }
                // Only an explicit request activates the window; automatic startup preserves the user's target.
                window.Activate();
            })) return Loc.T("Finish recording or processing before viewing what's new.");
            if (_whatsNewSession.Error is { } error) AppDiagnostics.Write("whats-new.preferences.failed", error);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DismissWhatsNew();
            AppDiagnostics.Write("whats-new.show.failed", ex);
            return Loc.T("What's new could not be opened. Try again.");
        }
    }

    internal void SuppressStartupWhatsNew(bool deferDismiss = false)
    {
        _whatsNewSession.Suppress();
        if (_whatsNewWindow is null) return;
        if (DispatcherQueue.HasThreadAccess && !deferDismiss) DismissWhatsNew();
        else DispatcherQueue.TryEnqueue(DismissWhatsNew);
    }

    private void DismissWhatsNew()
    {
        var window = _whatsNewWindow;
        _whatsNewWindow = null;
        if (window is null) return;
        try { window.AppWindow.Hide(); window.Close(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { AppDiagnostics.Write("whats-new.close.failed", ex); }
    }

    private void ObserveWhatsNewActivity()
    {
        // Startup model loading is not user activity. A capture, file, recorder or workflow reservation is.
        if (_dictation.HasActiveUserWork) SuppressStartupWhatsNew();
    }

    internal void QueueStartupWhatsNew() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        try
        {
            if (!_whatsNewSession.Pending) return;
            var busy = WhatsNewBusy || _settingsWindow is not null
                || _notice?.IsShowing == true || _reviewWindows.Count > 0;
            if (busy) { _whatsNewSession.Suppress(); return; }
            var window = CreateWhatsNewWindow();
            // A redirected activation may arrive from another thread while the XAML tree is constructed.
            if (!_whatsNewSession.TryShow(false, window.ShowWithoutTakingFocus)) DismissWhatsNew();
            if (_whatsNewSession.Error is { } error) AppDiagnostics.Write("whats-new.preferences.failed", error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DismissWhatsNew();
            AppDiagnostics.Write("whats-new.show.failed", ex);
        }
    });
}
