using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

internal sealed class CliSettingsView : UserControl
{
    internal CliSettingsView()
    {
        var installation = new CliInstallation(WinUIProfile.Root);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(SettingsHelp.Label(Loc.T("typewhisper command"),
            Loc.T("Use typewhisper from your terminal. Commands that connect to TypeWhisper require its HTTP API to be running. Installation adds it to your user PATH. Open a new terminal after installing. If an older CLI appears earlier in PATH, Windows will still use that version. You can run this version directly:\n{0}",
            installation.GetState().InstallPath)));
        var status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        body.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var install = new HandCursorButton { Content = Loc.T("Install"), Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        var remove = new HandCursorButton { Content = Loc.T("Remove"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        actions.Children.Add(install);
        actions.Children.Add(remove);
        body.Children.Add(actions);
        ToolTipService.SetToolTip(install, Loc.T("Adds typewhisper to your user PATH. Open a new terminal after installing."));
        var examples = new StackPanel { Spacing = 8 };
        examples.Children.Add(SettingsHelp.Label(Loc.T("PowerShell examples"),
            Loc.T("Copy a command into a new PowerShell terminal. Enable the HTTP API under API server first. Replace the example audio path with your own file.")));
        void Example(string title, string command)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var text = new TextBlock { Text = command, FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);
            var copy = new HandCursorButton { Content = Loc.T("Copy"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
            AutomationProperties.SetName(copy, Loc.T("Copy command: {0}", title));
            ToolTipService.SetToolTip(copy, title);
            copy.Click += (_, _) =>
            {
                try { var data = new DataPackage(); data.SetText(command); Clipboard.SetContent(data); status.Text = Loc.T("Command copied."); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = Loc.T("Could not copy the command. Try again."); }
            };
            Grid.SetColumn(copy, 1); row.Children.Add(copy); examples.Children.Add(row);
        }
        Example(Loc.T("Check status"), "typewhisper status");
        Example(Loc.T("List models"), "typewhisper models");
        Example(Loc.T("Last transcript"), "typewhisper last");
        Example(Loc.T("Transcribe a file"), @"typewhisper transcribe ""C:\Audio\recording.wav""");
        body.Children.Add(examples);
        var documentation = new HyperlinkButton { Content = Loc.T("CLI documentation"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        body.Children.Add(documentation);
        void Refresh(string? message = null)
        {
            var state = installation.GetState();
            examples.Visibility = state.Installed ? Visibility.Visible : Visibility.Collapsed;
            documentation.NavigateUri = new Uri("https://www.typewhisper.com/en/docs/windows/cli/");
            install.Content = state.Installed ? Loc.T("Update") : Loc.T("Install");
            install.IsEnabled = state.Bundled;
            remove.IsEnabled = state.CanRemove;
            remove.Visibility = state.CanRemove ? Visibility.Visible : Visibility.Collapsed;
            status.Text = message ?? (state.Installed
                ? state.InPath ? Loc.T("Installed") : Loc.T("Installed. Update to add typewhisper to PATH.")
                : state.Bundled ? Loc.T("Not installed") : Loc.T("CLI is not included in this build."));
            ToolTipService.SetToolTip(status, state.InstallPath);
        }
        async Task RunAsync(bool installing)
        {
            install.IsEnabled = remove.IsEnabled = false;
            try
            {
                await Task.Run(() => { if (installing) installation.Install(); else installation.Remove(); });
                Refresh(installing ? Loc.T("Installed. Open a new terminal to use typewhisper.") : Loc.T("Removed."));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Refresh(installing ? Loc.T("Could not install CLI: {0}", ex.Message) : Loc.T("Could not remove CLI: {0}", ex.Message));
            }
        }
        install.Click += async (_, _) => await RunAsync(true);
        remove.Click += async (_, _) => await RunAsync(false);
        Loaded += (_, _) => Refresh();
        Refresh();
        Content = body;
    }
}
