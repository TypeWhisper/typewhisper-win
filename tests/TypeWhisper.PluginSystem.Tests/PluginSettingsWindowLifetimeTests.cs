namespace TypeWhisper.PluginSystem.Tests;

using System.Windows.Controls;
using TypeWhisper.Windows.Views;

public sealed class PluginSettingsWindowLifetimeTests
{
    [Fact]
    public void PluginSettingsWindow_CanBeCreatedAndReleasesHostedViewWhenClosed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            failure = Record.Exception(() =>
            {
                var settingsView = new UserControl();
                var dialog = new PluginSettingsWindow("Test Plugin", settingsView);

                Assert.Same(settingsView, dialog.SettingsContent.Content);
                dialog.Show();
                dialog.Close();
                Assert.Null(dialog.SettingsContent.Content);

                var reopenedDialog = new PluginSettingsWindow("Test Plugin", settingsView);
                Assert.Same(settingsView, reopenedDialog.SettingsContent.Content);
                reopenedDialog.Show();
                reopenedDialog.Close();
                Assert.Null(reopenedDialog.SettingsContent.Content);
            });
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Plugin settings window did not close.");

        Assert.Null(failure);
    }

    [Fact]
    public void PluginSettingsWindow_IsResizableScrollableAndOwnedModal()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "PluginSettingsWindow.xaml");
        var section = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml.cs");
        var dialog = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "PluginSettingsWindow.xaml.cs");

        Assert.Contains("Width=\"640\"", xaml);
        Assert.Contains("Height=\"560\"", xaml);
        Assert.Contains("WindowStartupLocation=\"CenterOwner\"", xaml);
        Assert.Contains("ResizeMode=\"CanResizeWithGrip\"", xaml);
        Assert.Contains("ShowInTaskbar=\"False\"", xaml);
        Assert.Contains("<ui:FluentWindow", xaml);
        Assert.Contains("ExtendsContentIntoTitleBar=\"True\"", xaml);
        Assert.Contains("WindowBackdropType=\"None\"", xaml);
        Assert.Contains("Style=\"{StaticResource WizardShellStyle}\"", xaml);
        Assert.Contains("<ui:TitleBar", xaml);
        Assert.Contains("Title=\"{Binding Title, RelativeSource={RelativeSource AncestorType={x:Type ui:FluentWindow}}}\"", xaml);
        Assert.Contains("ShowMinimize=\"False\"", xaml);
        Assert.Contains("ShowMaximize=\"False\"", xaml);
        Assert.Contains("Foreground=\"{DynamicResource TextFillColorPrimaryBrush}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding RelativeSource={RelativeSource Self}, Path=Title}\"", xaml);
        Assert.DoesNotContain("x:Name=\"PluginNameText\"", xaml);
        Assert.Contains("<ScrollViewer", xaml);
        Assert.Contains("plugin.SettingsView is null", section);
        Assert.Contains("Owner = owner", section);
        Assert.Contains("dialog.ShowDialog();", section);
        Assert.Contains("Title = $\"{Loc.Instance[\"Settings.WindowTitle\"]} – {pluginName}\";", dialog);
        Assert.Contains("SettingsContent.Content = null;", dialog);
    }

    [Fact]
    public void SuccessfulFreshInstall_FocusesPluginBeforeOpeningSettings()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml.cs");
        var handler = TestFile.ExtractBlock(source, "private async void OnInstallPluginClick");

        Assert.Contains("plugin.InstallState != PluginInstallState.Installed", handler);
        Assert.Contains("FocusInstalledPlugin(plugin.Id)", handler);
        Assert.Contains("OpenPluginSettings(installedPlugin);", handler);
        Assert.True(
            handler.IndexOf("FocusInstalledPlugin(plugin.Id)", StringComparison.Ordinal)
            < handler.IndexOf("OpenPluginSettings(installedPlugin);", StringComparison.Ordinal));
    }
}
