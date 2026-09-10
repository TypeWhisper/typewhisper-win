using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class WorkflowsView
{
    private string _draftIcon = "workflow";
    private readonly HandCursorButton _iconPicker = new();
    private readonly List<HandCursorButton> _iconChoices = [];

    private void InitializeIconPicker()
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var i = 0; i < 6; i++) grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        for (var i = 0; i < 5; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var flyout = new Flyout { Content = grid };
        foreach (var (id, label) in WorkflowIcons.All)
        {
            var index = _iconChoices.Count;
            var button = new HandCursorButton { Width = 40, Height = 40, Padding = new Thickness(8),
                Content = new TypeWhisperGlyph { Kind = id, Width = 22, Height = 22 }, Tag = id };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += (_, _) => { SetDraftIcon(id); flyout.Hide(); UpdateConfigurationState(); _iconPicker.Focus(FocusState.Keyboard); };
            button.KeyDown += (_, e) =>
            {
                var next = e.Key switch
                {
                    global::Windows.System.VirtualKey.Left => index % 6 > 0 ? index - 1 : index,
                    global::Windows.System.VirtualKey.Right => index % 6 < 5 ? index + 1 : index,
                    global::Windows.System.VirtualKey.Up => index >= 6 ? index - 6 : index,
                    global::Windows.System.VirtualKey.Down => index < 24 ? index + 6 : index,
                    _ => -1
                };
                if (next < 0) return;
                _iconChoices[next].Focus(FocusState.Keyboard); e.Handled = true;
            };
            Grid.SetRow(button, index / 6); Grid.SetColumn(button, index % 6);
            grid.Children.Add(button); _iconChoices.Add(button);
        }
        flyout.Opened += (_, _) => _iconChoices.First(button => (string)button.Tag == _draftIcon).Focus(FocusState.Keyboard);
        _iconPicker.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
        _iconPicker.HorizontalAlignment = HorizontalAlignment.Left;
        _iconPicker.Flyout = flyout;
        ConfigIconHost.Child = _iconPicker;
        SetDraftIcon("workflow");
    }

    private void SetDraftIcon(string icon)
    {
        _draftIcon = WorkflowIcons.Normalize(icon);
        var label = WorkflowIcons.All.First(choice => choice.Id == _draftIcon).Label;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new TypeWhisperGlyph { Kind = _draftIcon, Width = 20, Height = 20 });
        content.Children.Add(new TextBlock { Text = "Icon · " + label, VerticalAlignment = VerticalAlignment.Center });
        _iconPicker.Content = content;
        AutomationProperties.SetName(_iconPicker, "Choose workflow icon: " + label);
        foreach (var button in _iconChoices)
        {
            var selected = (string)button.Tag == _draftIcon;
            button.Style = (Style)Application.Current.Resources[selected ? "PrimaryButtonStyle" : "SecondaryButtonStyle"];
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        }
    }
}
