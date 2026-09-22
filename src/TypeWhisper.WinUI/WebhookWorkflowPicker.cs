using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Host-owned workflow selection preserves the plugin's existing newline-delimited contract.
// No SDK upgrade is required, and older hosts can still edit the saved filter.
internal sealed class WebhookWorkflowPicker : UserControl
{
    private readonly SortedSet<string> _selected;
    private readonly Action<string> _changed;
    private readonly int _maximum;
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 };
    private static string L(string en, string de) => CultureInfo.CurrentUICulture.Name.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? de : en;

    internal WebhookWorkflowPicker(string value, int maximum, Action<string> changed)
    {
        _selected = new(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
        _maximum = maximum; _changed = changed;
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(_summary);
        var arrow = new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1); row.Children.Add(arrow);
        var button = new HandCursorButton { Content = row, MinHeight = 40, Padding = new(10, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0) };
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = BuildChoices();
        button.Flyout = flyout;
        Content = button;
        void RefreshSummary()
        {
            _summary.Text = _selected.Count == 0 ? L("All workflows", "Alle Workflows") : string.Join(", ", _selected);
            AutomationProperties.SetName(button, L("Choose workflows: ", "Workflows auswählen: ") + _summary.Text);
            ToolTipService.SetToolTip(button, _summary.Text);
        }
        _refreshSummary = RefreshSummary;
        RefreshSummary();
    }
    private readonly Action _refreshSummary;

    private FrameworkElement BuildChoices()
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 280, MaxWidth = 460 };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        AutomationProperties.SetLiveSetting(note, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        string[] names;
        try
        {
            names = new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read()
                .Select(w => w.Name).Where(n => !n.Contains('\r') && !n.Contains('\n') && n == n.Trim())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            note.Text = L("Workflows could not be loaded. Your saved selection is unchanged. Close and reopen this list to retry.",
                "Workflows konnten nicht geladen werden. Deine gespeicherte Auswahl bleibt erhalten. Liste zum Wiederholen erneut öffnen.");
            panel.Children.Add(note); return panel;
        }
        var all = new CheckBox { Content = L("All workflows", "Alle Workflows"), IsChecked = _selected.Count == 0 };
        panel.Children.Add(all);
        var hint = new TextBlock { Text = L("All includes dictation without a workflow. Changes take effect after saving.",
            "Alle schließt Diktate ohne Workflow ein. Änderungen gelten nach dem Speichern."),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        panel.Children.Add(hint);
        panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["HairlineBrush"] });
        var list = new StackPanel { Spacing = 4 };
        var boxes = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        var synchronizing = false;
        void Synchronize()
        {
            synchronizing = true;
            all.IsChecked = _selected.Count == 0;
            foreach (var (name, box) in boxes) box.IsChecked = _selected.Contains(name);
            synchronizing = false;
            _refreshSummary();
        }
        void CommitDraft() { _changed(string.Join("\n", _selected)); note.Text = ""; Synchronize(); }
        all.Checked += (_, _) => { if (synchronizing) return; _selected.Clear(); CommitDraft(); };
        all.Unchecked += (_, _) => { if (!synchronizing && _selected.Count == 0) Synchronize(); };
        foreach (var name in names.Concat(_selected.Except(names, StringComparer.Ordinal)))
        {
            var available = names.Contains(name, StringComparer.Ordinal);
            var title = name + (available ? "" : L(" (unavailable)", " (nicht verfügbar)"));
            var box = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }, IsChecked = _selected.Contains(name) };
            AutomationProperties.SetName(box, title);
            boxes.Add(name, box); list.Children.Add(box);
            box.Checked += (_, _) =>
            {
                if (synchronizing) return;
                _selected.Add(name);
                if (string.Join("\n", _selected).Length > _maximum)
                { _selected.Remove(name); note.Text = L("Too many workflows selected.", "Zu viele Workflows ausgewählt."); Synchronize(); return; }
                CommitDraft();
            };
            box.Unchecked += (_, _) =>
            {
                if (synchronizing) return;
                // An empty persisted filter means all. Never silently widen delivery by clearing the last item.
                if (_selected.Count == 1 && _selected.Contains(name))
                {
                    note.Text = L("Select another workflow, choose All workflows, or turn off Send after dictation.",
                        "Wähle einen anderen Workflow oder Alle Workflows, oder schalte Nach dem Diktieren senden aus.");
                    Synchronize(); return;
                }
                _selected.Remove(name); CommitDraft();
            };
        }
        if (names.Length == 0) list.Children.Add(new TextBlock { Text = L("No workflows yet. Create one in Workflows.",
            "Noch keine Workflows. Lege zuerst einen unter Workflows an."), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(note);
        return panel;
    }
}
