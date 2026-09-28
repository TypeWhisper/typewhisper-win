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
        var heading = SettingsHelp.Label("Diagnostics",
            "Records dictation steps, timings, settings choices such as the engine, and error types on this device. " +
            "It never contains dictated text, clipboard contents, window titles, file names or error messages, and it is never sent automatically.", 18);
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        var enabled = AppToggleSwitch.Create(false);
        AutomationProperties.SetName(enabled, "Keep diagnostic log");
        Grid.SetColumn(enabled, 1);
        header.Children.Add(enabled);
        body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = "Export the log and attach it when you contact support. Turning the log off deletes it.",
            FontSize = 13, TextWrapping = TextWrapping.Wrap });

        var details = new StackPanel { Spacing = 12 };
        body.Children.Add(details);
        var retention = new ChoicePicker();
        retention.Configure("Keep entries for", "history", "Diagnostic log retention");
        details.Children.Add(new TextBlock { Text = "Keep entries for", FontSize = 13 });
        details.Children.Add(retention);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var export = new HandCursorButton { Content = "Export…", Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        AutomationProperties.SetName(export, "Export diagnostic log");
        var clear = new HandCursorButton { Content = "Delete log", Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        AutomationProperties.SetName(clear, "Delete diagnostic log");
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
                Days(days), "Older entries are deleted automatically."))], preferences.RetentionDays.ToString());
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
                enabled.IsOn ? "Diagnostic log is on." : "Diagnostic log is off. The existing log was deleted.");
        };
        retention.SelectionChanged += id =>
        {
            if (refreshing || !int.TryParse(id, out var days)) return;
            var preferences = AppDiagnostics.Preferences?.Current ?? new();
            Apply(preferences with { RetentionDays = days }, $"Entries older than {Days(days)} are deleted automatically.");
        };
        export.Click += async (_, _) =>
        {
            export.IsEnabled = false;
            try
            {
                var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                { Title = "Export diagnostic log", SuggestedFileName = "typewhisper-diagnostics-" + DateTime.Now.ToString("yyyy-MM-dd") };
                picker.FileTypeChoices.Add("JSON Lines", new List<string> { ".jsonl" });
                var file = await picker.PickSaveFileAsync();
                if (file is null) { status.Text = "Export canceled."; return; }
                var count = await Task.Run(() => AppDiagnostics.Export(file.Path));
                status.Text = (count == 1 ? "1 entry" : $"{count:N0} entries") + " exported to " + file.Path;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status.Text = "The diagnostic log could not be exported. Choose another location and try again.";
            }
            finally { export.IsEnabled = true; }
        };
        clear.Click += (_, _) =>
        {
            AppDiagnostics.Clear();
            status.Text = "Diagnostic log deleted. New entries are recorded while the log is on.";
        };
        Refresh();
        Content = body;
    }

    private static string Days(int days) => days == 1 ? "1 day" : $"{days} days";
}
