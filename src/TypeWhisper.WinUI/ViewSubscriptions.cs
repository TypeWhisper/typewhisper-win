using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Own external event subscriptions only while the view is in the visual tree.
internal static class ViewSubscriptions
{
    internal static void Attach(FrameworkElement view, Action subscribe, Action unsubscribe)
    {
        var attached = false;
        void Load()
        {
            if (attached) return;
            attached = true;
            try { subscribe(); }
            catch { attached = false; unsubscribe(); throw; }
        }
        void Unload()
        {
            if (!attached) return;
            attached = false;
            unsubscribe();
        }
        view.Loaded += (_, _) => Load();
        view.Unloaded += (_, _) => Unload();
        if (view.IsLoaded) Load();
    }

    // Whether the view is in the visual tree and neither it nor an ancestor is collapsed. Settings keeps its
    // hidden pages in the tree, so a loaded view is not necessarily shown.
    internal static bool IsShown(FrameworkElement view)
    {
        if (!view.IsLoaded) return false;
        for (DependencyObject? element = view; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
}
