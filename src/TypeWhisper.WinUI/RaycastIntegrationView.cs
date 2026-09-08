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
        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "Raycast Extension", FontSize = 14 });
        var link = new HyperlinkButton { Content = "Learn more", Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTipService.SetToolTip(link, "Start dictation, search History and switch profiles from Raycast. Requires the HTTP API to be running.");
        row.Children.Add(link);
        var installed = false;
        Loaded += async (_, _) =>
        {
            try { installed = await Launcher.QueryUriSupportAsync(Extension, LaunchQuerySupportType.Uri) == LaunchQuerySupportStatus.Available; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { installed = false; }
            link.Content = installed ? "Open in Raycast" : "Learn more";
        };
        link.Click += async (_, _) =>
        {
            link.IsEnabled = false;
            try
            {
                if (!installed || !await Launcher.LaunchUriAsync(Extension)) await Launcher.LaunchUriAsync(Store);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { link.Content = "Could not open Raycast — try again"; }
            finally { link.IsEnabled = true; }
        };
        Content = row;
    }
}
