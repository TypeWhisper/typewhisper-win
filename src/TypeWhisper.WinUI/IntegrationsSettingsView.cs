using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.System;

namespace TypeWhisper.WinUI;

// The macOS Integrations section: one row per companion with its icon, a short explanation and one action.
internal sealed class IntegrationsSettingsView : UserControl
{
    private static readonly Uri RaycastExtension = new("raycast://extensions/SeoFood/typewhisper");
    private static readonly Uri RaycastStore = new("https://www.raycast.com/SeoFood/typewhisper");
    private static readonly Uri BrowserExtension = new("https://www.typewhisper.com/extensions#browser");

    internal IntegrationsSettingsView()
    {
        var rows = new SettingsRows();
        rows.Children.Add(RaycastRow());
        rows.Children.Add(BrowserRow());
        Content = rows;
    }

    private SettingsRow RaycastRow()
    {
        var action = Action(Loc.T("Learn more"));
        var row = new SettingsRow().Icon("keyboard").Set(Loc.T("Raycast Extension"),
            Loc.T("TypeWhisper works with Raycast. Start dictation and more directly from your launcher. Requires the HTTP API to be running."), "", action);
        var installed = false;
        Loaded += async (_, _) =>
        {
            try { installed = await Launcher.QueryUriSupportAsync(RaycastExtension, LaunchQuerySupportType.Uri) == LaunchQuerySupportStatus.Available; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { installed = false; }
            action.Content = installed ? Loc.T("Open in Raycast") : Loc.T("Learn more");
            if (installed) row.Description = Loc.T("Start dictation, search History and switch profiles from Raycast. Requires the HTTP API to be running.");
        };
        action.Click += async (_, _) =>
        {
            action.IsEnabled = false;
            row.Status = "";
            try
            {
                if (!installed || !await Launcher.LaunchUriAsync(RaycastExtension)) await Launcher.LaunchUriAsync(RaycastStore);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { row.Status = Loc.T("Could not open Raycast — try again"); }
            finally { action.IsEnabled = true; }
        };
        return row;
    }

    private static SettingsRow BrowserRow()
    {
        var action = Action(Loc.T("Learn more"));
        var row = new SettingsRow().Icon("globe").Set(Loc.T("Browser Extension"),
            Loc.T("Dictate into text fields in Chrome and Firefox, and let website workflows match the page you are on. Requires the HTTP API to be running."), "", action);
        action.Click += async (_, _) =>
        {
            row.Status = "";
            try { if (!await Launcher.LaunchUriAsync(BrowserExtension)) row.Status = Loc.T("The browser could not be opened."); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { row.Status = Loc.T("The browser could not be opened."); }
        };
        return row;
    }

    private static HandCursorButton Action(string text) =>
        new() { Content = text, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
}
