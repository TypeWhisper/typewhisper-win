using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Read-only view of the machine policy for Settings > Advanced. Values come from the validated snapshot and
// sensitive keys show a placeholder. Nothing here writes policy; Refresh only re-reads it.
internal sealed class ManagedPolicyView : UserControl
{
    private readonly TextBlock _summary = Text(13, "TextBrush");
    private readonly Grid _entries = new() { ColumnSpacing = 16, RowSpacing = 0 };
    private readonly TextBlock _issuesTitle = Text(13, "TextBrush");
    private readonly StackPanel _issues = new() { Spacing = 4 };
    private readonly TextBlock _meta = Text(12, "MutedBrush");

    /// <summary>Set by the main window: conflicts between enforced provider/model pairs and the installed providers.</summary>
    internal static Func<IEnumerable<string>>? RuntimeIssues { get; set; }

    internal ManagedPolicyView()
    {
        var body = new StackPanel { Spacing = 10 };
        var refresh = new HandCursorButton { Content = Loc.T("Refresh"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        refresh.Click += (_, _) => WinUIManagedPolicy.RequestRefresh();
        var row = new SettingsRow("ManagedPolicy").Set(Loc.T("Organization policy"),
            Loc.T("Settings your organization enforces or recommends through Windows policy. Enforced values cannot be changed here; recommended values are defaults you can change."), refresh);
        body.Children.Add(row);
        body.Children.Add(_summary);
        body.Children.Add(_entries);
        _issuesTitle.Text = Loc.T("Policy problems");
        body.Children.Add(_issuesTitle);
        body.Children.Add(_issues);
        body.Children.Add(_meta);
        Content = body;
        ViewSubscriptions.Attach(this, () => { WinUIManagedPolicy.Changed += OnChanged; Render(WinUIManagedPolicy.Current); },
            () => WinUIManagedPolicy.Changed -= OnChanged);
        Render(WinUIManagedPolicy.Current);
    }

    private void OnChanged(ManagedPolicySnapshot snapshot) => DispatcherQueue.TryEnqueue(() => Render(snapshot));

    private void Render(ManagedPolicySnapshot snapshot)
    {
        _summary.Text = snapshot.Status switch
        {
            ManagedPolicyReadStatus.Unreadable => Loc.T("Your organization's policy could not be read. Managed settings keep their last known values."),
            _ when !snapshot.IsManaged => Loc.T("No organization policy applies to this device."),
            _ => Loc.T("Managed settings on this device:")
        };
        var rows = new List<(string Key, string Scope, string Value)>();
        foreach (var definition in ManagedPolicyKeys.All)
        {
            if (snapshot.InvalidEnforcedKeys.Contains(definition.Name))
                rows.Add((definition.Name, Loc.T("Enforced"), Loc.T("Invalid value, the setting stays blocked")));
            else if (snapshot.Enforced(definition.Name) is { } enforced)
                rows.Add((definition.Name, Loc.T("Enforced"), Describe(definition, enforced)));
            if (snapshot.Recommended(definition.Name) is { } recommended)
                rows.Add((definition.Name, Loc.T("Recommended"), Describe(definition, recommended)));
        }
        RenderTable(rows);
        _issues.Children.Clear();
        foreach (var issue in snapshot.Issues)
            _issues.Children.Add(Text(12, "MutedBrush", issue.Message));
        IEnumerable<string> runtime = [];
        try { runtime = (RuntimeIssues?.Invoke() ?? []).ToArray(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.view.runtime-issues", ex); }
        foreach (var issue in runtime)
            _issues.Children.Add(Text(12, "MutedBrush", issue));
        _issuesTitle.Visibility = _issues.Visibility = _issues.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var watching = WinUIManagedPolicy.Watching ? Loc.T("Watching for changes") : Loc.T("Checked every 30 seconds");
        _meta.Text = snapshot.Revision == 0 ? watching
            : Loc.T("Last change {0}", snapshot.ReadAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) + " · " + watching;
    }

    private static string Describe(ManagedPolicyDefinition definition, ManagedPolicyValue value)
    {
        if (definition.Sensitive) return "••••••";
        return value.Kind switch
        {
            ManagedPolicyValueKind.Boolean => value.IsTrue ? Loc.T("On") : Loc.T("Off"),
            ManagedPolicyValueKind.Enumeration => definition.Name switch
            {
                ManagedPolicyKeys.UpdateMode => value.Number switch
                {
                    (int)AppUpdateMode.External => Loc.T("External deployment"),
                    (int)AppUpdateMode.CheckOnly => Loc.T("Check only"),
                    _ => Loc.T("User-initiated installation")
                },
                ManagedPolicyKeys.UpdateChannel => value.Number switch
                {
                    (int)AppUpdateChannel.Daily => Loc.T("Daily"),
                    (int)AppUpdateChannel.ReleaseCandidate => Loc.T("Release Candidate"),
                    _ => Loc.T("Stable")
                },
                _ => value.Number.ToString(CultureInfo.InvariantCulture)
            },
            ManagedPolicyValueKind.List => value.Items is { Count: > 0 } items ? string.Join(", ", items) : Loc.T("None"),
            _ => value.Text ?? ""
        };
    }

    // A three-column table: setting, scope, value. Header and rows share the column widths; rows are separated by
    // hairlines like the settings rows around them.
    private void RenderTable(List<(string Key, string Scope, string Value)> rows)
    {
        _entries.Children.Clear();
        _entries.RowDefinitions.Clear();
        _entries.ColumnDefinitions.Clear();
        _entries.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (rows.Count == 0) return;
        _entries.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _entries.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _entries.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var headers = new[] { Loc.T("Setting"), Loc.T("Scope"), Loc.T("Value") };
        AddRow(0, headers, header: true);
        for (var i = 0; i < rows.Count; i++)
            AddRow(i + 1, [rows[i].Key, rows[i].Scope, rows[i].Value], header: false);
    }

    private void AddRow(int row, string[] cells, bool header)
    {
        _entries.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var column = 0; column < cells.Length; column++)
        {
            var text = Text(12, header ? "MutedBrush" : "TextBrush", cells[column]);
            if (header) text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (column == 1 && !header) text.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            // The border draws the hairline below every row; the cell keeps a little air above and below the text.
            var cell = new Border
            {
                Child = text,
                Padding = new Thickness(0, 6, 0, 6),
                BorderThickness = new Thickness(0, 0, 0, row == 0 ? 1 : 0.5),
                BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"]
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            _entries.Children.Add(cell);
        }
    }

    private static TextBlock Text(double size, string brush, string text = "") => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[brush]
    };
}
