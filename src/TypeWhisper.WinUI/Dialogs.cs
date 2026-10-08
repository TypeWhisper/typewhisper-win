using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Every ContentDialog is shown through here. WinUI opens at most one ContentDialog per XamlRoot
// and throws from ShowAsync when a second one is requested while the first is still up. The gate
// answers such a request with None instead: the second click is dropped, not queued, so no
// confirmation pops up after the user has already moved on. This is what the call sites did by
// hand with flags and catch blocks.
internal static class Dialogs
{
    // XamlRoot compares by the identity of the underlying WinRT object, so this holds one entry per window.
    private static readonly HashSet<XamlRoot> Open = [];

    internal static async Task<ContentDialogResult> ShowAsync(FrameworkElement host, ContentDialog dialog)
    {
        dialog.XamlRoot ??= host.XamlRoot;
        if (dialog.RequestedTheme == ElementTheme.Default) dialog.RequestedTheme = host.ActualTheme;
        // An unloaded host has no XamlRoot; ShowAsync would throw instead of showing anything.
        if (dialog.XamlRoot is not { } root) return ContentDialogResult.None;
        lock (Open) { if (!Open.Add(root)) return ContentDialogResult.None; }
        try { return await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // A dialog opened outside this gate is still up, or the root is already closing.
            AppDiagnostics.Write("dialog.show.rejected", ex);
            return ContentDialogResult.None;
        }
        finally { lock (Open) Open.Remove(root); }
    }

    // Confirmations keep the close button as the safe default; a destructive primary action gets
    // the red confirm style.
    internal static ContentDialog Confirmation(string title, object content, string primaryText, bool destructive = false, string? closeText = null)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = content, PrimaryButtonText = primaryText,
            CloseButtonText = closeText ?? Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        if (destructive) dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DestructiveConfirmButtonStyle"];
        return dialog;
    }

    internal static async Task<bool> ConfirmAsync(FrameworkElement host, string title, object content, string primaryText, bool destructive = false, string? closeText = null) =>
        await ShowAsync(host, Confirmation(title, content, primaryText, destructive, closeText)) == ContentDialogResult.Primary;
}
