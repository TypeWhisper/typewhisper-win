using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class DiagnosticsSettingsView : UserControl
{
    internal const string SettingKey = "Diagnostics";

    internal DiagnosticsSettingsView()
    {
        var body = new StackPanel { Spacing = 12, Tag = SettingKey };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = SettingsHelp.Label(Loc.T("Keep diagnostic log"),
            Loc.T("Records dictation steps, timings, settings choices such as the engine, and error types on this device. It never contains dictated text, clipboard contents, window titles, file names or error messages, and it is never sent automatically."));
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        var enabled = AppToggleSwitch.Create(false);
        AutomationProperties.SetName(enabled, Loc.T("Keep diagnostic log"));
        Grid.SetColumn(enabled, 1);
        header.Children.Add(enabled);
        body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = Loc.T("Export the log and attach it when you contact support. Turning the log off deletes it."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap });

        var details = new StackPanel { Spacing = 12 };
        body.Children.Add(details);
        var retention = new ChoicePicker();
        retention.Configure(Loc.T("Keep entries for"), "history", Loc.T("Diagnostic log retention"));
        details.Children.Add(new TextBlock { Text = Loc.T("Keep entries for"), FontSize = 13 });
        details.Children.Add(retention);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var export = new HandCursorButton { Content = Loc.T("Export…"), Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        AutomationProperties.SetName(export, Loc.T("Export diagnostic log"));
        var clear = new HandCursorButton { Content = Loc.T("Delete log"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        AutomationProperties.SetName(clear, Loc.T("Delete diagnostic log"));
        actions.Children.Add(export);
        actions.Children.Add(clear);
        details.Children.Add(actions);
        var status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Text = AppDiagnostics.Preferences?.Error ?? "" };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        body.Children.Add(status);

        var refreshing = false;
        void Refresh()
        {
            refreshing = true;
            var preferences = AppDiagnostics.Preferences?.Current ?? new();
            enabled.IsOn = preferences.Enabled;
            retention.SetOptions([.. DiagnosticLogPreferences.RetentionChoices.Select(days => new Choice(days.ToString(),
                Days(days), Loc.T("Older entries are deleted automatically.")))], preferences.RetentionDays.ToString());
            details.Visibility = preferences.Enabled ? Visibility.Visible : Visibility.Collapsed;
            refreshing = false;
        }
        void Apply(DiagnosticLogPreferences preferences, string saved)
        {
            status.Text = AppDiagnostics.Configure(preferences) ?? saved;
            Refresh();
        }
        enabled.Toggled += (_, _) =>
        {
            if (refreshing) return;
            var preferences = AppDiagnostics.Preferences?.Current ?? new();
            Apply(preferences with { Enabled = enabled.IsOn },
                enabled.IsOn ? Loc.T("Diagnostic log is on.") : Loc.T("Diagnostic log is off. The existing log was deleted."));
        };
        retention.SelectionChanged += id =>
        {
            if (refreshing || !int.TryParse(id, out var days)) return;
            var preferences = AppDiagnostics.Preferences?.Current ?? new();
            Apply(preferences with { RetentionDays = days }, Loc.T("Entries older than {0} are deleted automatically.", Days(days)));
        };
        export.Click += async (_, _) =>
        {
            export.IsEnabled = false;
            try
            {
                var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                { Title = Loc.T("Export diagnostic log"), SuggestedFileName = "typewhisper-diagnostics-" + DateTime.Now.ToString("yyyy-MM-dd") };
                picker.FileTypeChoices.Add("JSON Lines", new List<string> { ".jsonl" });
                var file = await picker.PickSaveFileAsync();
                if (file is null) { status.Text = Loc.T("Export canceled."); return; }
                var count = await Task.Run(() => AppDiagnostics.Export(file.Path));
                status.Text = count == 1 ? Loc.T("1 entry exported to {0}", file.Path) : Loc.T("{0:N0} entries exported to {1}", count, file.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status.Text = Loc.T("The diagnostic log could not be exported. Choose another location and try again.");
            }
            finally { export.IsEnabled = true; }
        };
        clear.Click += (_, _) =>
        {
            status.Text = AppDiagnostics.Clear()
                ? Loc.T("Diagnostic log deleted. New entries are recorded while the log is on.")
                : Loc.T("The diagnostic log could not be deleted because another program is using it. Close that program and try again.");
        };
        Refresh();
        Content = body;
    }

    private static string Days(int days) => days == 1 ? Loc.T("1 day") : Loc.T("{0} days", days);
}
