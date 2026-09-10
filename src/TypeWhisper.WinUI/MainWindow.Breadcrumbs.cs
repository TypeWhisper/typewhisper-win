using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private Crumb[]? _titleCrumbs;
    private readonly Crumb[] _launcherCrumbs = [new("Quick Launch")];

    private void UpdateTitleBreadcrumbs()
    {
        if (WindowRoot.XamlRoot is null || _closing) return;
        var source = Breadcrumbs.LoadedSources.FirstOrDefault(crumbs =>
            !crumbs.IsTitleDestination && crumbs.XamlRoot == WindowRoot.XamlRoot && AncestorsVisible(crumbs));
        var items = source?.Items ?? _launcherCrumbs;
        if (source is not null)
        {
            // Keep the source and its callbacks alive, without a second visible row.
            source.Visibility = Visibility.Collapsed;
            source.Margin = new Thickness(0);
        }
        TitleBreadcrumbs.MaxWidth = Math.Max(160, WindowRoot.ActualWidth - 220);
        if (ReferenceEquals(_titleCrumbs, items)) return;
        _titleCrumbs = items;
        TitleBreadcrumbs.SetItems(items);
    }

    private static bool AncestorsVisible(DependencyObject source)
    {
        // The source itself is intentionally collapsed once mirrored.
        for (var parent = VisualTreeHelper.GetParent(source); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
}
