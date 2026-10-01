using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveApplicationUpdateSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers, AppUpdateController controller)
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
        void Refresh()
        {
            picker.SetOptions([
                new("Stable", Loc.T("Stable"), Loc.T("Released versions")),
                new("Daily", Loc.T("Daily"), Loc.T("Latest development builds")),
                new("ReleaseCandidate", Loc.T("Release Candidate"), Loc.T("Testing before release"))
            ], controller.Preferences.Channel.ToString());
            picker.IsEnabled = !controller.Busy;
            check.IsEnabled = controller.CanCheck;
            install.IsEnabled = !controller.Busy;
            install.Visibility = controller.Offer is null ? Visibility.Collapsed : Visibility.Visible;
            install.Content = controller.Offer?.IsDowngrade == true ? Loc.T("Install older version and restart") : Loc.T("Download and restart");
            status.Text = controller.Status;
        }
        picker.SelectionChanged += id => { if (Enum.TryParse<AppUpdateChannel>(id, out var channel)) controller.Select(channel); };
        check.Click += async (_, _) => await controller.CheckAsync();
        install.Click += async (_, _) => await controller.InstallAsync();
        row.Loaded += (_, _) => { controller.Changed += Refresh; Refresh(); };
        row.Unloaded += (_, _) => controller.Changed -= Refresh;
        row.Children.Add(picker); row.Children.Add(status); row.Children.Add(actions); pickers.Add(picker);
        Refresh();
    }
}
