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
        row.Children.Add(SettingsHelp.Label("App updates", "Stable contains released versions. Daily contains the latest development builds. Release Candidate contains versions being tested before release. Changing channels does not install anything until you choose Download and restart. Only compatible WinUI releases are offered."));
        row.Children.Add(new TextBlock { Text = "Installed version: " + WindowsApplicationUpdates.CurrentVersion });
        var picker = new ChoicePicker(); picker.Configure("Update channel", "download", "Update channel");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var check = new HandCursorButton { Content = "Check for updates", Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        var install = new HandCursorButton { Content = "Download and restart", Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(check); actions.Children.Add(install);
        void Refresh()
        {
            picker.SetOptions([
                new("Stable", "Stable", "Released versions"),
                new("Daily", "Daily", "Latest development builds"),
                new("ReleaseCandidate", "Release Candidate", "Testing before release")
            ], controller.Preferences.Channel.ToString());
            picker.IsEnabled = !controller.Busy;
            check.IsEnabled = controller.CanCheck;
            install.IsEnabled = !controller.Busy;
            install.Visibility = controller.Offer is null ? Visibility.Collapsed : Visibility.Visible;
            install.Content = controller.Offer?.IsDowngrade == true ? "Install older version and restart" : "Download and restart";
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
