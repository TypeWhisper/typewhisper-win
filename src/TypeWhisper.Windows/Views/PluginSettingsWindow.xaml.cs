using System.Windows.Controls;
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
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SettingsContent.Content = null;
        Closed -= OnClosed;
    }
}
