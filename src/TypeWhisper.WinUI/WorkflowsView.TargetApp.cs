using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// "Send to app": a dictation workflow can insert its text into another app than the one in front (#764), for example a
// chat app next to a game. The field suggests the apps that have a window open right now.
public sealed partial class WorkflowsView
{
    private readonly AutoSuggestBox ConfigTargetApp = new() { MinHeight = 40, MaxSuggestionListHeight = 240 };
    private readonly SettingsRow ConfigTargetAppSection = new();
    private IReadOnlyList<string> _runningApps = [];
    private bool _runningAppsLoading;

    private void BuildTargetApp(SettingsCard card)
    {
        ConfigTargetApp.PlaceholderText = Loc.T("Where you dictate (default)");
        AutomationProperties.SetName(ConfigTargetApp, Loc.T("Send to app"));
        ConfigTargetApp.GotFocus += (_, _) => _ = LoadRunningAppsAsync();
        ConfigTargetApp.TextChanged += (_, e) =>
        {
            if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) SuggestRunningApps();
            if (!_loadingConfiguration && _page == Page.Configuration) UpdateConfigurationState();
        };
        ConfigTargetApp.SuggestionChosen += (_, e) => { if (e.SelectedItem is string app) ConfigTargetApp.Text = app; };
        card.Children.Add(ConfigTargetAppSection.Set(Loc.T("Send to app"),
            Loc.T("Leave empty to insert where you dictate. Otherwise TypeWhisper brings this app’s window to the front, inserts the text there, presses Enter if chosen below and returns to the window you were in, for example to send chat messages to Discord while a game is in front. Enter the process name without .exe."),
            (FrameworkElement?)null).Below(ConfigTargetApp));
    }

    private string ConfigTargetAppText => ConfigTargetApp.Text.Trim();
    // Only text that dictation inserts can go to another app; a plugin action receives it instead.
    private bool ConfigSendsText => ConfigTrigger.SelectedId is "DictationHotkey" or "App" or "Website" or "Global"
        && string.IsNullOrEmpty(ConfigActionTarget.SelectedId);
    private string? ConfigSelectedTargetApp => ConfigSendsText && ConfigTargetAppText.Length > 0 ? ConfigTargetAppText : null;
    private string? TargetAppError => ConfigSelectedTargetApp is { } app && !WorkflowTargetApp.IsValid(app)
        ? Loc.T("Enter the app’s process name, such as discord, without a path or .exe.") : null;

    private void LoadTargetApp(string? app)
    {
        ConfigTargetApp.Text = app ?? "";
        ConfigTargetApp.ItemsSource = null;
    }

    private void UpdateTargetAppState() =>
        ConfigTargetAppSection.Visibility = ConfigSendsText ? Visibility.Visible : Visibility.Collapsed;

    private async Task LoadRunningAppsAsync()
    {
        if (_runningAppsLoading) return;
        _runningAppsLoading = true;
        // Called without being awaited, so a failure is logged here; the field then just offers no suggestions.
        try { _runningApps = await Task.Run(RunningApps); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("workflow.target-app.list-failed", ex); }
        finally { _runningAppsLoading = false; }
        if (_closing || _page != Page.Configuration) return;
        SuggestRunningApps();
    }

    private void SuggestRunningApps()
    {
        var text = ConfigTargetAppText;
        var apps = _runningApps.Where(app => app.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        ConfigTargetApp.ItemsSource = apps;
        ConfigTargetApp.IsSuggestionListOpen = apps.Length > 0 && ConfigTargetApp.FocusState != FocusState.Unfocused;
    }

    // Programs with a window, so the app can be picked by name. Protected or ended processes are left out.
    private static IReadOnlyList<string> RunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero) names.Add(process.ProcessName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    continue;
                }
            }
        }
        return names.Take(100).ToArray();
    }
}
