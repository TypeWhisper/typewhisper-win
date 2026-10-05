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

    internal DiagnosticsSettingsView(LocalDictationSession session, WinUIHttpApi api)
    {
        var body = new SettingsRows();
        var reportExport = new HandCursorButton
        {
            Content = Loc.T("Export diagnostics…"),
            Style = (Style)Application.Current.Resources["PrimaryButtonStyle"]
        };
        AutomationProperties.SetName(reportExport, Loc.T("Export support report"));
        var reportRow = new SettingsRow(SettingKey).Set(Loc.T("Support report"),
            Loc.T("Save a report to attach when you contact support."),
            Loc.T("Includes app and system details, microphone names and access, models, plugins, workflow metadata, settings and any retained diagnostic log. Excludes API keys, audio, transcripts, prompt contents and file paths. Nothing is sent automatically."), reportExport);
        body.Children.Add(reportRow);
        reportExport.Click += async (_, _) =>
        {
            reportExport.IsEnabled = false;
            try
            {
                var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
                {
                    Title = Loc.T("Export support report"),
                    SuggestedFileName = "typewhisper-diagnostics-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss")
                };
                picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
                var file = await picker.PickSaveFileAsync();
                if (file is null) { reportRow.Status = Loc.T("Export canceled."); return; }
                var report = await SupportDiagnosticsExporter.CaptureAsync(session, api);
                await Task.Run(() => report.Export(file.Path));
                reportRow.Status = report.CollectionErrors.Count == 0
                    ? Loc.T("Support report exported to {0}", file.Path)
                    : Loc.T("Support report exported to {0}. Some details were unavailable and are listed in the report.", file.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reportRow.Status = Loc.T("The support report could not be exported. Choose another location and try again.");
            }
            finally { reportExport.IsEnabled = true; }
        };

        var enabled = AppToggleSwitch.Create(false);
        AutomationProperties.SetName(enabled, Loc.T("Keep diagnostic log"));
        var log = new SettingsRow().Set(Loc.T("Keep diagnostic log"),
            Loc.T("Export the log and attach it when you contact support. Turning the log off deletes it."),
            Loc.T("Records dictation steps, timings, settings choices such as the engine, and error types on this device. It never contains dictated text, clipboard contents, window titles, file names or error messages, and it is never sent automatically."), enabled);
        log.Status = AppDiagnostics.Preferences?.Error ?? "";
        body.Children.Add(log);

        var retention = new ChoicePicker();
        retention.Configure(Loc.T("Keep entries for"), "history", Loc.T("Diagnostic log retention"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var export = new HandCursorButton { Content = Loc.T("Export…"), Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        AutomationProperties.SetName(export, Loc.T("Export diagnostic log"));
        var clear = new HandCursorButton { Content = Loc.T("Delete log"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        AutomationProperties.SetName(clear, Loc.T("Delete diagnostic log"));
        actions.Children.Add(export);
        actions.Children.Add(clear);
        var details = new SettingsRow().Set(Loc.T("Keep entries for"), control: retention);
        details.Below(actions);
        body.Children.Add(details);

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
        void Apply(DiagnosticLogPreferences preferences)
        {
            log.Status = AppDiagnostics.Configure(preferences) ?? "";
            details.Status = "";
            Refresh();
        }
        enabled.Toggled += (_, _) =>
        {
            if (refreshing) return;
            var preferences = AppDiagnostics.Preferences?.Current ?? new();
            Apply(preferences with { Enabled = enabled.IsOn });
        };
        retention.SelectionChanged += id =>
        {
            if (refreshing || !int.TryParse(id, out var days)) return;
            var preferences = AppDiagnostics.Preferences?.Current ?? new();
            Apply(preferences with { RetentionDays = days });
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
                if (file is null) { details.Status = Loc.T("Export canceled."); return; }
                var count = await Task.Run(() => AppDiagnostics.Export(file.Path));
                details.Status = count == 1 ? Loc.T("1 entry exported to {0}", file.Path) : Loc.T("{0:N0} entries exported to {1}", count, file.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                details.Status = Loc.T("The diagnostic log could not be exported. Choose another location and try again.");
            }
            finally { export.IsEnabled = true; }
        };
        clear.Click += (_, _) =>
        {
            details.Status = AppDiagnostics.Clear()
                ? Loc.T("Diagnostic log deleted. New entries are recorded while the log is on.")
                : Loc.T("The diagnostic log could not be deleted because another program is using it. Close that program and try again.");
        };
        Refresh();
        Content = body;
    }

    private static string Days(int days) => days == 1 ? Loc.T("1 day") : Loc.T("{0} days", days);
}
