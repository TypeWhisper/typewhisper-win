using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using global::Windows.System;

namespace TypeWhisper.WinUI;

internal sealed class RaycastIntegrationView : UserControl
{
    private static readonly Uri Extension = new("raycast://extensions/SeoFood/typewhisper");
    private static readonly Uri Store = new("https://www.raycast.com/SeoFood/typewhisper");
    internal RaycastIntegrationView()
    {
        var link = new HyperlinkButton { Content = Loc.T("Learn more") };
        var row = new SettingsRow().Set(Loc.T("Raycast Extension"),
            Loc.T("Start dictation, search History and switch profiles from Raycast. Requires the HTTP API to be running."), link);
        var installed = false;
        Loaded += async (_, _) =>
        {
            try { installed = await Launcher.QueryUriSupportAsync(Extension, LaunchQuerySupportType.Uri) == LaunchQuerySupportStatus.Available; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { installed = false; }
            link.Content = installed ? Loc.T("Open in Raycast") : Loc.T("Learn more");
        };
        link.Click += async (_, _) =>
        {
            link.IsEnabled = false;
            try
            {
                if (!installed || !await Launcher.LaunchUriAsync(Extension)) await Launcher.LaunchUriAsync(Store);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { link.Content = Loc.T("Could not open Raycast — try again"); }
            finally { link.IsEnabled = true; }
        };
        Content = row;
    }
}
