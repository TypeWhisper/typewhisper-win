using Microsoft.UI.Xaml;

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
}
