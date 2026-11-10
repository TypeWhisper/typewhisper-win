using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// A workflow can send its text to another app than the one the dictation started in, such as a chat app next to a game
// (#764). TypeWhisper brings that app's window to the front, inserts there like any dictation and then returns to the
// window that was in front before. An app closed to the notification area is started again, which makes it open its own
// window, and goes back there afterwards.
internal sealed partial class LocalDictationSession
{
    // How long an app started again from the notification area gets to open its window.
    private static readonly TimeSpan ReopenTimeout = TimeSpan.FromSeconds(5);
    // How long the delivery waits for the app to focus its text field. Focus on a button, for example, does not change;
    // a chat app then still moves the paste into its message field.
    private static readonly TimeSpan TextFieldWait = TimeSpan.FromMilliseconds(400);

    // The target app that had no window to insert into, so the notice can name it; null otherwise.
    private string? _targetAppMissing;

    // The app the matched workflow sends this dictation to; null inserts where the dictation started.
    private string? TargetAppAtStart => _workflowAtStart?.TargetApp;

    // The window the text goes to, the one to return to afterwards (Zero when the app was already in front) and whether
    // the window was hidden in the notification area before.
    private readonly record struct TargetAppDelivery(IntPtr Window, IntPtr ReturnTo, bool WasHidden);

    /// <summary>The window to return to while a workflow sends the text to another app, so the overlay stays on its
    /// display instead of following the target app; Zero otherwise.</summary>
    internal IntPtr TargetAppReturnWindow { get; private set; }

    private async Task<TargetAppDelivery?> ActivateTargetAppAsync(string app)
    {
        TargetAppReturnWindow = NativeMethods.GetForegroundWindow();
        try
        {
            var delivery = await ActivateTargetAppCoreAsync(app, TargetAppReturnWindow);
            if (delivery is null) TargetAppReturnWindow = IntPtr.Zero;
            return delivery;
        }
        catch { TargetAppReturnWindow = IntPtr.Zero; throw; }
    }

    private async Task<TargetAppDelivery?> ActivateTargetAppCoreAsync(string app, IntPtr front)
    {
        var candidate = TargetAppWindow.Find(app);
        var hidden = candidate is not { Visible: true };
        // Showing a hidden window from outside leaves the app's focus wherever it was, often on a button. Started again,
        // an app that runs once opens its window itself, with the focus in its text field, as after a click on its
        // Start menu entry.
        if ((hidden ? await ReopenTargetAppAsync(app, candidate?.ProcessId, front) : candidate) is not { } found)
        {
            _targetAppMissing = app;
            AppDiagnostics.Write("delivery.target-app.missing");
            return null;
        }
        // An app started again may already have taken the front; it still goes back to the notification area afterwards.
        if (!hidden && front == found.Window) return new(found.Window, IntPtr.Zero, false);
        var delivery = new TargetAppDelivery(found.Window, front, hidden);
        try
        {
            if (!await WindowActivation.ActivateAsync(found.Window, found.ProcessId, "delivery.target-app", _operationCancellation.Token))
            {
                AppDiagnostics.Write("delivery.target-app.activation-failed");
                if (hidden) TargetAppWindow.Hide(found.Window);
                return null;
            }
            AppDiagnostics.Write("delivery.target-app.activated");
            await WaitForTextFieldAsync(found);
            return delivery;
        }
        catch (OperationCanceledException)
        {
            // Canceled once the app may be in front: leave the screen as after a delivery.
            await UndoTargetAppAsync(delivery);
            throw;
        }
    }

    // Returns to the window the user came from and puts an app from the notification area back there, after a
    // cancel that came before the text was inserted.
    private async Task UndoTargetAppAsync(TargetAppDelivery delivery)
    {
        await ReturnFromTargetAppCoreAsync(delivery);
        if (delivery.WasHidden && NativeMethods.GetForegroundWindow() != delivery.Window) TargetAppWindow.Hide(delivery.Window);
        AppDiagnostics.Write("delivery.target-app.canceled");
    }

    // Starts the running app's program again and waits until it shows a window. Only an app that is running is started,
    // so a workflow never launches an app the user closed. Programs that the shell cannot start, such as packaged
    // Store apps, report the app as not open.
    private async Task<TopLevelWindow?> ReopenTargetAppAsync(string app, uint? processId, IntPtr front)
    {
        var running = processId ?? FirstProcessId(app);
        if (running is not { } process || ProcessImagePath(process) is not { } path)
        {
            AppDiagnostics.Write("delivery.target-app.not-running");
            return null;
        }
        try
        {
            // The started app may take the front only if TypeWhisper sent the last input.
            WindowActivation.GrantForeground();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Write("delivery.target-app.reopen-failed", ex);
            return null;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < ReopenTimeout)
        {
            try { await Task.Delay(100, _operationCancellation.Token); }
            catch (OperationCanceledException)
            {
                // The app may have opened its window by now; it goes back to the notification area.
                if (TargetAppWindow.Find(app) is { Visible: true } opened) await UndoTargetAppAsync(new(opened.Window, front, true));
                throw;
            }
            if (TargetAppWindow.Find(app) is { Visible: true } window)
            {
                AppDiagnostics.Write($"delivery.target-app.reopened delayMs={clock.ElapsedMilliseconds}");
                return window;
            }
        }
        AppDiagnostics.Write("delivery.target-app.reopen-timeout");
        return null;
    }

    private static uint? FirstProcessId(string app)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(app);
        try { return processes.Where(item => item.Id != Environment.ProcessId).Select(item => (uint?)item.Id).FirstOrDefault(); }
        finally { foreach (var item in processes) item.Dispose(); }
    }

    // An app moves the focus into its text field a moment after it comes to the front, and one shown from the
    // notification area first restores its content. A paste sent before then gets lost, and Enter is refused because
    // the field changed under it. The field lock's capture waits until a text field of that window has the focus, at
    // most 400 ms; usually it takes a few dozen milliseconds. Without one, the paste goes ahead as usual.
    private async Task WaitForTextFieldAsync(TopLevelWindow target)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_operationCancellation.Token);
        limit.CancelAfter(TextFieldWait);
        OriginalDictationField? field = null;
        try { field = await OriginalDictationField.CaptureAsync(target.Window, target.ProcessId, limit.Token); }
        catch (OperationCanceledException) when (!_operationCancellation.Token.IsCancellationRequested) { }
        AppDiagnostics.Write($"delivery.target-app.field accepted={field is not null} delayMs={started.ElapsedMilliseconds}");
        if (field is not null) await field.DisposeAsync();
    }

    // Whether the focused element of the target window is a password field. UI Automation runs outside the UI thread, and
    // a read that does not answer within its timeout counts as no password field, like the regular paste path.
    private static async Task<bool> FocusedPasswordFieldAsync(IntPtr window)
    {
        var read = Task.Run(() =>
        {
            Interop.UIAutomationClient.IUIAutomation2? automation = null;
            Interop.UIAutomationClient.IUIAutomationElement? element = null;
            try
            {
                if (NativeMethods.GetForegroundWindow() != window) return false;
                automation = (Interop.UIAutomationClient.IUIAutomation2)new Interop.UIAutomationClient.CUIAutomation8();
                automation.ConnectionTimeout = 200; automation.TransactionTimeout = 200;
                element = automation.GetFocusedElement();
                return element is not null && element.CurrentIsPassword != 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
            finally
            {
                if (element is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(element);
                if (automation is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(automation);
            }
        });
        var password = await Task.WhenAny(read, Task.Delay(300)) == read && await read;
        if (password) AppDiagnostics.Write("delivery.target-app.password");
        return password;
    }

    // Waits until the app had the time the paste gets to take the text, and Enter followed, then returns to the window
    // that was in front. A user who switched elsewhere meanwhile keeps that window. A window that was in the notification
    // area goes back there once the user is back in the window they came from.
    private async Task ReturnFromTargetAppAsync(TargetAppDelivery delivery)
    {
        try { await ReturnFromTargetAppCoreAsync(delivery); }
        finally { TargetAppReturnWindow = IntPtr.Zero; }
    }

    private async Task ReturnFromTargetAppCoreAsync(TargetAppDelivery delivery)
    {
        if (delivery.ReturnTo == IntPtr.Zero) return;
        await _inserter.Restored;
        await Task.Delay(150);
        if (_disposed || NativeMethods.GetForegroundWindow() != delivery.Window)
        {
            AppDiagnostics.Write("delivery.target-app.return-skipped");
            return;
        }
        var process = ForegroundWindowHistory.ProcessOf(delivery.ReturnTo);
        if (process == 0) { AppDiagnostics.Write("delivery.target-app.return-closed"); return; }
        try
        {
            var returned = await WindowActivation.ActivateAsync(delivery.ReturnTo, process, "delivery.target-app.return", CancellationToken.None);
            AppDiagnostics.Write($"delivery.target-app.returned accepted={returned}");
            if (returned && delivery.WasHidden)
            {
                TargetAppWindow.Hide(delivery.Window);
                AppDiagnostics.Write("delivery.target-app.hidden");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("delivery.target-app.return-failed", ex); }
    }
}
