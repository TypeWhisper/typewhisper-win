using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class CliSettingsView : UserControl
{
    internal CliSettingsView()
    {
        var installation = new CliInstallation(WinUIProfile.Root);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SettingsHelp.Label("Command Line Tool",
            "Use typewhisper from your terminal. Commands that connect to TypeWhisper require its HTTP API to be running. " +
            "Installation adds it to your user PATH. Open a new terminal after installing. " +
            "If an older CLI appears earlier in PATH, Windows will still use that version. You can run this version directly:\n" +
            installation.GetState().InstallPath, 18));
        var status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        body.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var install = new HandCursorButton { Content = "Install", Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        var remove = new HandCursorButton { Content = "Remove", Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        actions.Children.Add(install);
        actions.Children.Add(remove);
        body.Children.Add(actions);
        ToolTipService.SetToolTip(install, "Adds typewhisper to your user PATH. Open a new terminal after installing.");
        void Refresh(string? message = null)
        {
            var state = installation.GetState();
            install.Content = state.Installed ? "Update" : "Install";
            install.IsEnabled = state.Bundled;
            remove.IsEnabled = state.CanRemove;
            remove.Visibility = state.CanRemove ? Visibility.Visible : Visibility.Collapsed;
            status.Text = message ?? (state.Installed
                ? state.InPath ? "Installed" : "Installed. Update to add typewhisper to PATH."
                : state.Bundled ? "Not installed" : "CLI is not included in this build.");
            ToolTipService.SetToolTip(status, state.InstallPath);
        }
        async Task RunAsync(bool installing)
        {
            install.IsEnabled = remove.IsEnabled = false;
            try
            {
                await Task.Run(() => { if (installing) installation.Install(); else installation.Remove(); });
                Refresh(installing ? "Installed. Open a new terminal to use typewhisper." : "Removed.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Refresh($"Could not {(installing ? "install" : "remove")} CLI: {ex.Message}");
            }
        }
        install.Click += async (_, _) => await RunAsync(true);
        remove.Click += async (_, _) => await RunAsync(false);
        Loaded += (_, _) => Refresh();
        Refresh();
        Content = body;
    }
}
