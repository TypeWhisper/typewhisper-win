using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using global::Windows.System;

namespace TypeWhisper.WinUI;

internal sealed record Tab(string Id, string Label);

/// <summary>Shared, left-aligned section tabs with a single keyboard entry point.</summary>
public sealed class TabBar : UserControl
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly List<HandCursorButton> _buttons = [];
    internal event Action<string>? SelectionChanged;
    internal string SelectedId { get; private set; } = "";
    internal Control SelectedControl => _buttons.FirstOrDefault(b => (string)b.Tag == SelectedId) ?? (Control)this;

    public TabBar()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        IsTabStop = false;
        Content = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled, IsTabStop = false };
    }
    internal void SetItems(IReadOnlyList<Tab> tabs, string selectedId)
    {
        if (tabs.Count == 0 || tabs.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != tabs.Count)
            throw new ArgumentException("Tabs require unique identifiers.", nameof(tabs));
        _panel.Children.Clear(); _buttons.Clear();
        foreach (var tab in tabs)
        {
            var button = new HandCursorButton { Content = tab.Label, Tag = tab.Id, MinHeight = 34 };
            button.Click += (_, _) => Activate(button);
            button.KeyDown += (_, e) =>
            {
                int index = _buttons.IndexOf(button);
                var next = e.Key switch
                {
                    VirtualKey.Left => (index + _buttons.Count - 1) % _buttons.Count,
                    VirtualKey.Right => (index + 1) % _buttons.Count,
                    VirtualKey.Home => 0, VirtualKey.End => _buttons.Count - 1, _ => -1
                };
                if (next < 0) return;
                e.Handled = true;
                Activate(_buttons[next]);
            };
            _buttons.Add(button); _panel.Children.Add(button);
        }
        SetSelected(tabs.Any(t => t.Id == selectedId) ? selectedId : tabs[0].Id);
    }
    internal void SetSelected(string id)
    {
        if (!_buttons.Any(b => (string)b.Tag == id)) return;
        SelectedId = id;
        foreach (var button in _buttons)
        {
            var selected = (string)button.Tag == id;
            button.IsTabStop = selected;
            button.Style = (Style)Application.Current.Resources[selected ? "PrimaryButtonStyle" : "IconButtonStyle"];
            AutomationProperties.SetName(button, button.Content + (selected ? ", selected" : ""));
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        }
    }
    private void Activate(HandCursorButton button)
    {
        var id = (string)button.Tag;
        var changed = id != SelectedId;
        SetSelected(id);
        button.Focus(FocusState.Programmatic);
        button.StartBringIntoView();
        if (changed) SelectionChanged?.Invoke(id);
    }
}
