using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using global::Windows.System;

namespace TypeWhisper.WinUI;

public sealed partial class HistoryView
{
    internal void HandleActionKey(KeyRoutedEventArgs e)
    {
        if (e.Handled || _closing || _acting || _loading || _dialogs.Count > 0 ||
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
            return;
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox) return;
        foreach (var modifier in new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift,
                     VirtualKey.LeftWindows, VirtualKey.RightWindows })
            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

        // Enter on a tab-focused button keeps the platform's normal invocation.
        if (e.Key == VirtualKey.Enter && focused is Button) return;
        var args = new RoutedEventArgs();
        if (e.Key == VirtualKey.R && RetryAudioCleanupButton.Visibility == Visibility.Visible && RetryAudioCleanupButton.IsEnabled)
            RetryAudioCleanup_Click(this, args);
        else if (IsReading)
        {
            if (e.Key == VirtualKey.Enter && CopyButton.Visibility == Visibility.Visible) Copy_Click(this, args);
            else if (EntryActions.Visibility == Visibility.Visible && e.Key == VirtualKey.E) Edit_Click(this, args);
            else if (EntryActions.Visibility == Visibility.Visible && e.Key == VirtualKey.X) Export_Click(this, args);
            else if (EntryActions.Visibility == Visibility.Visible && e.Key == VirtualKey.Delete) Delete_Click(this, args);
            else if (ReadAloudButton.Visibility == Visibility.Visible && e.Key == VirtualKey.P) ReadAloud_Click(this, args);
            else if (AudioActions.Visibility == Visibility.Visible && e.Key == VirtualKey.A && PlayAudioButton.IsEnabled) PlayAudio_Click(this, args);
            else if (AudioActions.Visibility == Visibility.Visible && e.Key == VirtualKey.F && ShowAudioButton.IsEnabled) ShowAudio_Click(this, args);
            else return;
        }
        else if (e.Key == VirtualKey.S && SelectEntriesButton.IsEnabled) SelectEntries_Click(this, args);
        else if (_selecting && e.Key == VirtualKey.A && SelectAllShownButton.IsEnabled) SelectAll_Click(this, args);
        else if (_selecting && e.Key == VirtualKey.X && ExportSelectedButton.IsEnabled) ExportSelected_Click(this, args);
        else if (_selecting && e.Key == VirtualKey.Delete && DeleteSelectedButton.IsEnabled) DeleteSelected_Click(this, args);
        else return;
        e.Handled = true;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();
}
