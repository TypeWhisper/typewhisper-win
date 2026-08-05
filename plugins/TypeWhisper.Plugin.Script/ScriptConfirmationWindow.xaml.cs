using System.Windows;
using System.Windows.Input;

namespace TypeWhisper.Plugin.Script;

internal partial class ScriptConfirmationWindow : Window
{
    private ScriptConfirmationWindow(
        string title,
        string message,
        string primary,
        string? secondary,
        string cancel,
        bool primaryIsDanger)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary ?? "";
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = cancel;
        if (primaryIsDanger && TryFindResource("ScriptDangerButtonStyle") is Style dangerStyle)
            PrimaryButton.Style = dangerStyle;
    }

    internal ConfirmationChoice Choice { get; private set; } = ConfirmationChoice.Cancel;

    internal static ScriptConfirmationWindow CreateUnsaved(
        string title,
        string message,
        string save,
        string discard,
        string cancel) => new(title, message, save, discard, cancel, false);

    internal static ScriptConfirmationWindow CreateRemove(
        string title,
        string message,
        string remove,
        string cancel) => new(title, message, remove, null, cancel, true);

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationChoice.Primary;
        DialogResult = true;
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationChoice.Secondary;
        DialogResult = false;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationChoice.Cancel;
        Close();
    }
}
