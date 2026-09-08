using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using global::Windows.System;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private Dictionary<string, string> _commandShortcuts = new(StringComparer.Ordinal);
    private static string CommandShortcutsPath => WinUIProfile.DataPath("quick-launch-shortcuts.json");
    private string CommandShortcut(Command command) => _commandShortcuts.GetValueOrDefault(command.Key, command.Shortcut.Replace("Ctrl ,", "Ctrl+,"));
    private bool LauncherCommandsVisible => !_historyOpen && !_recorderOpen && !_workflowsOpen &&
        !_pluginsOpen && !_marketplaceOpen && !UtilityOpen && !LexiconOpen && !FileTranscriptionOpen;

    private void LoadCommandShortcuts()
    {
        try
        {
            if (File.Exists(CommandShortcutsPath))
                _commandShortcuts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CommandShortcutsPath)) ?? new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Debug.WriteLine(error); }
    }

    private static bool CommandModifierDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static string? CommandChord(VirtualKey key)
    {
        if (CommandModifierDown(VirtualKey.LeftWindows) || CommandModifierDown(VirtualKey.RightWindows)) return null;
        var value = (int)key;
        var label = value is >= 65 and <= 90 or >= 48 and <= 57 ? ((char)value).ToString()
            : value is >= 112 and <= 123 ? "F" + (value - 111)
            : value == 0xBC ? "," : null;
        if (label is null) return null;
        return (CommandModifierDown(VirtualKey.Control) ? "Ctrl+" : "") +
            (CommandModifierDown(VirtualKey.Menu) ? "Alt+" : "") +
            (CommandModifierDown(VirtualKey.Shift) ? "Shift+" : "") + label;
    }

    private bool HandleCommandShortcut(KeyRoutedEventArgs e)
    {
        if (!LauncherCommandsVisible || _closing || _isSearchEditing || !string.IsNullOrEmpty(SearchBox.Text)) return false;
        var focused = FocusManager.GetFocusedElement(WindowRoot.XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox && !ReferenceEquals(focused, SearchBox)) return false;
        var chord = CommandChord(e.Key);
        if (chord is null) return false;
        var command = LauncherCommands().FirstOrDefault(candidate => CommandShortcut(candidate) == chord);
        if (command is null) return false;
        _selected = command;
        RunSelected();
        return true;
    }

    private async Task ConfigureCommandShortcutAsync(Command command)
    {
        var value = CommandShortcut(command);
        var capture = new TextBox { Text = value, IsReadOnly = true, PlaceholderText = "Press a shortcut", Margin = new Thickness(0, 8, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(capture, "Command shortcut");
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "Works while browsing Quick Launch. Click the search field to type a search.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(capture); body.Children.Add(notice);
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot, Title = "Shortcut for " + command.Title,
            Content = body, PrimaryButtonText = "Save", SecondaryButtonText = "Remove",
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        void Validate()
        {
            var conflict = LauncherCommands().FirstOrDefault(other => other.Key != command.Key && CommandShortcut(other) == value);
            notice.Text = value == "Ctrl+K" ? "Ctrl+K opens the Actions menu."
                : value == "Alt+F4" ? "Alt+F4 is reserved for closing windows."
                : conflict is not null ? "Already used by " + conflict.Title + "." : "";
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrEmpty(value) && string.IsNullOrEmpty(notice.Text);
        }
        capture.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Tab or VirtualKey.Escape or VirtualKey.Enter) return;
            e.Handled = true;
            if (CommandChord(e.Key) is not { } chord) return;
            value = chord; capture.Text = value; Validate();
        };
        dialog.Opened += (_, _) => capture.Focus(FocusState.Programmatic);
        Validate();
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;
            var next = new Dictionary<string, string>(_commandShortcuts, StringComparer.Ordinal)
            { [command.Key] = result == ContentDialogResult.Secondary ? "" : value };
            Directory.CreateDirectory(Path.GetDirectoryName(CommandShortcutsPath)!);
            var temporary = CommandShortcutsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, CommandShortcutsPath, overwrite: true);
            _commandShortcuts = next;
            SearchBox_TextChanged(SearchBox, null!);
            CompactResults.SelectedItem = FilteredItems.FirstOrDefault(item => item.Key == command.Key);
            _isSearchEditing = false;
            UpdateSearchPresentation();
            MetricsText.Text = "Command shortcut saved.";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { MetricsText.Text = "Could not save the shortcut. Please try again."; System.Diagnostics.Debug.WriteLine(error); }
    }
}
