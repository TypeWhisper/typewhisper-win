using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TypeWhisper.Windows.Services.Localization;
using Wpf.Ui.Controls;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Hosts a plugin-provided settings view in a modal window.
/// </summary>
public partial class PluginSettingsWindow : FluentWindow
{
    /// <summary>
    /// Initializes a new instance of the PluginSettingsWindow class.
    /// </summary>
    public PluginSettingsWindow(string pluginName, UserControl settingsView)
    {
        InitializeComponent();
        Title = $"{Loc.Instance["Settings.WindowTitle"]} – {pluginName}";
        SettingsContent.Content = settingsView;
        if (Program.UiAutomation.IsEnabled)
            Loaded += FitAutomationWindowToContent;
        Closed += OnClosed;
    }

    private void FitAutomationWindowToContent(object sender, RoutedEventArgs e)
    {
        Loaded -= FitAutomationWindowToContent;

        Dispatcher.BeginInvoke(ApplyAutomationWindowFit, DispatcherPriority.ContextIdle);
    }

    private void ApplyAutomationWindowFit()
    {
        SettingsContent.ClearValue(WidthProperty);
        SettingsContent.LayoutTransform = Transform.Identity;

        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        SizeToContent = SizeToContent.Manual;

        var contentScale = 1.0;
        for (var attempt = 0; attempt < 3 && SettingsScroll.ScrollableHeight > 0.5; attempt++)
        {
            if (SettingsScroll.ExtentHeight <= 0 || SettingsScroll.ViewportHeight <= 2)
                break;

            contentScale *= Math.Min(
                1.0,
                (SettingsScroll.ViewportHeight - 2) / SettingsScroll.ExtentHeight);
            SettingsContent.Width = SettingsScroll.ViewportWidth / contentScale;
            SettingsContent.LayoutTransform = new ScaleTransform(contentScale, contentScale);
            SizeToContent = SizeToContent.Height;
            UpdateLayout();
            SizeToContent = SizeToContent.Manual;
        }

        var workArea = SystemParameters.WorkArea;
        Top = Math.Clamp(Top, workArea.Top + 16, workArea.Bottom - ActualHeight - 16);
        SettingsScroll.ScrollToTop();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SettingsContent.Content = null;
        Closed -= OnClosed;
    }
}
