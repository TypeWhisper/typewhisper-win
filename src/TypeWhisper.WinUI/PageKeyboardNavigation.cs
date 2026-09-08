using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;
using global::Windows.System;

namespace TypeWhisper.WinUI;

/// <summary>Fallback navigation after the focused control has handled its own keys.</summary>
internal static class PageKeyboardNavigation
{
    internal static void Attach(FrameworkElement root, Func<DependencyObject, bool>? inactiveSearch = null)
    {
        root.KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)) return;
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(popup => popup.Child is not ToolTip)) return;
            foreach (var key in new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift, VirtualKey.LeftWindows, VirtualKey.RightWindows })
                if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                    .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

            ScrollViewer? containingScroll = null;
            for (var node = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
                 node is not null && node != root; node = VisualTreeHelper.GetParent(node))
            {
                // Never repurpose caret movement, list selection, slider values or calendar navigation.
                if (node is TextBox && inactiveSearch?.Invoke(node) != true) return;
                if (node is PasswordBox or RichEditBox or ComboBox or NumberBox or Slider or
                    ListViewBase or CalendarView or CalendarDatePicker or DatePicker or TimePicker or MediaPlayerElement) return;
                if (node is ScrollViewer scroll && scroll.ScrollableHeight > 0) containingScroll ??= scroll;
            }

            var viewer = containingScroll ?? VisibleScrollers(root)
                .OrderByDescending(scroll => scroll.ViewportHeight * scroll.ViewportWidth).FirstOrDefault();
            if (viewer is null) return;
            if (KeyboardScrollPolicy.Target((int)e.Key, viewer.VerticalOffset, viewer.ViewportHeight, viewer.ScrollableHeight) is not { } target) return;
            viewer.ChangeView(null, target, null, disableAnimation: true);
            e.Handled = true;
        };
    }

    private static IEnumerable<ScrollViewer> VisibleScrollers(DependencyObject root)
    {
        if (root is UIElement { Visibility: Visibility.Collapsed }) yield break;
        if (root is FrameworkElement element && (!element.IsLoaded || element.ActualHeight <= 0 || element.ActualWidth <= 0)) yield break;
        if (root is ScrollViewer { ScrollableHeight: > 0, IsEnabled: true } scroll) yield return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var viewer in VisibleScrollers(VisualTreeHelper.GetChild(root, index))) yield return viewer;
    }
}
