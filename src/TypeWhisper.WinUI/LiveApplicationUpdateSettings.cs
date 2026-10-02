using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveApplicationUpdateSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers, AppUpdateController controller,
        PortablePluginUpdates plugins, PluginAutoUpdatePreference automaticPreference, Func<bool> canUpdatePlugins, Func<Task<string?>> restart)
    {
        if (category != "Account & about") return;
        var row = content.Children.OfType<AccountView>().Single().UpdatePanel;
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear(); row.IsHitTestVisible = true;
        row.Children.Add(SettingsHelp.Label(Loc.T("App updates"), Loc.T("Stable contains released versions. Daily contains the latest development builds. Release Candidate contains versions being tested before release. Changing channels does not install anything until you choose Download and restart.")));
        row.Children.Add(new TextBlock { Text = Loc.T("Installed version: {0}", WindowsApplicationUpdates.CurrentVersion) });
        var picker = new ChoicePicker(); picker.Configure(Loc.T("Update channel"), "download", Loc.T("Update channel"));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var check = new HandCursorButton { Content = Loc.T("Check for updates"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        var install = new HandCursorButton { Content = Loc.T("Download and restart"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(check); actions.Children.Add(install);
        var pluginStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var updatePlugins = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], HorizontalAlignment = HorizontalAlignment.Left };
        var automatic = new ToggleSwitch { Header = Loc.T("Update plugins automatically"), OnContent = Loc.T("On"), OffContent = Loc.T("Off"), IsOn = automaticPreference.Enabled };
        var restarting = false;
        string? restartError = null;
        void Refresh()
        {
            picker.SetOptions([
                new("Stable", Loc.T("Stable"), Loc.T("Released versions")),
                new("Daily", Loc.T("Daily"), Loc.T("Latest development builds")),
                new("ReleaseCandidate", Loc.T("Release Candidate"), Loc.T("Testing before release"))
            ], controller.Preferences.Channel.ToString());
            picker.IsEnabled = !controller.Busy;
            // The plugin catalog can be checked even where app updates are unavailable.
            check.IsEnabled = !controller.Busy && !plugins.Checking && !plugins.Busy;
            install.IsEnabled = !controller.Busy;
            install.Visibility = controller.Offer is null ? Visibility.Collapsed : Visibility.Visible;
            install.Content = controller.Offer?.IsDowngrade == true ? Loc.T("Install older version and restart") : Loc.T("Download and restart");
            status.Text = controller.Status;
            var count = plugins.Available.Count;
            pluginStatus.Text = restartError ?? (plugins.Checking && !plugins.Busy ? Loc.T("Checking for updates…")
                : !plugins.Busy && plugins.Failed.Count == 0 && count > 0 ? Loc.T("Updates available: {0}", string.Join(", ", plugins.Available.Select(entry => entry.Name + " " + entry.Version)))
                : PluginUpdateStatus.Text(plugins) ?? (plugins.Checked ? Loc.T("All plugins are up to date.") : ""));
            automatic.IsOn = automaticPreference.Enabled;
            pluginStatus.Visibility = pluginStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            updatePlugins.Visibility = count > 0 || plugins.RestartRequired || plugins.Busy || restarting ? Visibility.Visible : Visibility.Collapsed;
            updatePlugins.Content = plugins.Busy ? Loc.T("Updating...") : restarting ? Loc.T("Restarting...")
                : count > 0 ? Loc.T("Update all plugins ({0})", count) : Loc.T("Restart now");
            updatePlugins.IsEnabled = !plugins.Busy && !restarting && !controller.Busy && canUpdatePlugins();
        }
        void RefreshOnUiThread() => row.DispatcherQueue.TryEnqueue(Refresh);
        picker.SelectionChanged += id => { if (Enum.TryParse<AppUpdateChannel>(id, out var channel)) controller.Select(channel); };
        check.Click += async (_, _) => await Task.WhenAll(controller.CheckAsync(), plugins.RefreshAsync());
        install.Click += async (_, _) => await controller.InstallAsync();
        updatePlugins.Click += async (_, _) =>
        {
            restartError = null;
            if (plugins.Available.Count > 0) await plugins.UpdateAsync();
            else
            {
                restarting = true; Refresh();
                try { restartError = await restart(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { restartError = Loc.T("Restart could not finish. Close and reopen TypeWhisper."); }
                finally { restarting = false; }
            }
            Refresh();
        };
        // A failed save switches the toggle back on the refresh.
        automatic.Toggled += (_, _) => { if (automatic.IsOn != automaticPreference.Enabled) { automaticPreference.Save(automatic.IsOn); Refresh(); } };
        row.Loaded += (_, _) => { controller.Changed += Refresh; plugins.Changed += RefreshOnUiThread; Refresh(); _ = plugins.RefreshAsync(); };
        row.Unloaded += (_, _) => { controller.Changed -= Refresh; plugins.Changed -= RefreshOnUiThread; };
        row.Children.Add(picker); row.Children.Add(status); row.Children.Add(actions); pickers.Add(picker);
        row.Children.Add(SettingsHelp.Label(Loc.T("Plugin updates"), Loc.T("Plugins are updated separately from the app. With automatic updates, TypeWhisper checks once a day and downloads new versions in the background. An updated plugin is used after TypeWhisper restarts.")));
        row.Children.Add(automatic); row.Children.Add(pluginStatus); row.Children.Add(updatePlugins);
        Refresh();
    }
}
