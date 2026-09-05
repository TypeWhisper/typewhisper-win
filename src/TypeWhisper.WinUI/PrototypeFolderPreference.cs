using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace TypeWhisper.WinUI;

// Folder selection only. Never creates, moves, scans or downloads model files.
public sealed class PrototypeFolderPreference : UserControl
{
    internal PrototypeFolderPreference(string key, string label, string value, Dictionary<string, string> values)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Copy(label, 14));
        var layout = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var input = new TextBox
        {
            Text = value, MinHeight = 40,
            Style = (Style)Application.Current.Resources["PrototypeSearchTextBoxStyle"]
        };
        AutomationProperties.SetName(input, $"Preference {key}");
        AutomationProperties.SetHelpText(input, "Choose a folder or enter its path. Empty uses the default folder. Preview only.");
        var placeholder = Copy("Default model folder", 13, true);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(10, 0, 4, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        void UpdatePlaceholder() => placeholder.Visibility = input.Text.Length == 0 && input.FocusState == FocusState.Unfocused
            ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid(); field.Children.Add(input); field.Children.Add(placeholder);
        var fieldBorder = new Border
        {
            Child = field, Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7)
        };
        layout.Children.Add(fieldBorder);
        var buttonContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonContent.Children.Add(new TypeWhisperGlyph { Kind = "folder", Width = 17, Height = 17 });
        buttonContent.Children.Add(Copy("Choose folder…", 12));
        var browse = new HandCursorButton
        {
            Content = buttonContent, MinHeight = 40,
            Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"]
        };
        AutomationProperties.SetName(browse, $"Choose {label}");
        Grid.SetColumn(browse, 1); layout.Children.Add(browse); panel.Children.Add(layout);
        var reset = new HandCursorButton
        {
            Content = "Use default", HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["PrototypeIconButtonStyle"]
        };
        AutomationProperties.SetName(reset, $"Use default {label}");
        reset.IsEnabled = value.Length > 0;
        panel.Children.Add(reset);
        var status = Copy("", 12, true); status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(status);
        input.TextChanged += (_, _) =>
        {
            values[key] = input.Text; reset.IsEnabled = input.Text.Length > 0;
            UpdatePlaceholder();
        };
        input.GotFocus += (_, _) => UpdatePlaceholder();
        input.LostFocus += (_, _) => UpdatePlaceholder();
        reset.Click += (_, _) =>
        {
            input.Text = ""; status.Text = "Default folder selected for this preview. No files were moved.";
            status.Visibility = Visibility.Visible; browse.Focus(FocusState.Keyboard);
        };
        var picking = false;
        browse.Click += async (_, _) =>
        {
            if (picking || XamlRoot is null) return;
            picking = true; browse.IsEnabled = input.IsEnabled = reset.IsEnabled = false;
            try
            {
                var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                {
                    Title = "Choose model storage folder", CommitButtonText = "Choose folder"
                };
                var result = await picker.PickSingleFolderAsync();
                if (result is not null)
                {
                    input.Text = result.Path;
                    status.Text = "Folder selected for this preview. No files were moved.";
                }
                else status.Text = "Selection canceled. The folder is unchanged.";
            }
            catch (Exception error)
            {
                status.Text = $"The folder dialog could not be opened (0x{error.HResult:X8}). You can enter the path above.";
            }
            finally
            {
                picking = false; browse.IsEnabled = input.IsEnabled = true;
                reset.IsEnabled = input.Text.Length > 0;
                status.Visibility = Visibility.Visible;
                if (IsLoaded) browse.Focus(FocusState.Keyboard);
            }
        };
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 480;
            Grid.SetColumn(browse, narrow ? 0 : 1); Grid.SetRow(browse, narrow ? 1 : 0);
            Grid.SetColumnSpan(fieldBorder, narrow ? 2 : 1);
            browse.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        };
        Content = panel; UpdatePlaceholder();
    }

    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
    private static TextBlock Copy(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(muted ? "MutedBrush" : "TextBrush")
    };
}
