using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Both pointer and keyboard context requests use the same current action state.
internal static class EntryActionMenu
{
    internal sealed record Action(string Label, System.Action Invoke, bool Enabled = true);

    internal static void Attach(FrameworkElement surface, Func<IEnumerable<Action>> actions)
    {
        MenuFlyout? openMenu = null;
        bool Show(DependencyObject? source, global::Windows.Foundation.Point? point)
        {
            if (openMenu is not null) return true;
            ListViewItem? row = null;
            var preserveNativeMenu = false;
            for (var node = source; node is not null && node != surface; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ListViewItem item) { row = item; break; }
                if (node is TextBox or PasswordBox or RichEditBox ||
                    node is TextBlock { IsTextSelectionEnabled: true } ||
                    node is Button or Border && node.ReadLocalValue(FrameworkElement.ContextFlyoutProperty) is MenuFlyout)
                    preserveNativeMenu = true;
            }
            if (row is null && preserveNativeMenu) return false;
            if (row is not null && ItemsControl.ItemsControlFromItemContainer(row) is ListView list)
            {
                if (!row.IsSelected)
                {
                    if (list.SelectionMode == ListViewSelectionMode.Single)
                        list.SelectedItem = list.ItemFromContainer(row);
                    else
                    {
                        list.SelectedItems.Clear();
                        list.SelectedItems.Add(list.ItemFromContainer(row));
                    }
                }
                row.Focus(FocusState.Programmatic);
            }
            var menu = Create(actions());
            if (menu.Items.Count == 0) return false;
            openMenu = menu;
            menu.Closed += (_, _) => openMenu = null;
            if (point is { } location)
                menu.ShowAt(surface, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = location });
            else menu.ShowAt(row ?? surface);
            return true;
        }
        surface.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(surface);
            if (point.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased &&
                Show(e.OriginalSource as DependencyObject, point.Position)) e.Handled = true;
        }), handledEventsToo: true);
        surface.IsRightTapEnabled = true;
        surface.AddHandler(UIElement.RightTappedEvent, new Microsoft.UI.Xaml.Input.RightTappedEventHandler((_, e) =>
        {
            if (Show(e.OriginalSource as DependencyObject, e.GetPosition(surface))) e.Handled = true;
        }), handledEventsToo: true);
        surface.ContextRequested += (_, e) =>
        {
            if (!e.Handled && Show(e.OriginalSource as DependencyObject, e.TryGetPosition(surface, out var point) ? point : null))
                e.Handled = true;
        };
        surface.KeyDown += (_, e) =>
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (!e.Handled && (e.Key == global::Windows.System.VirtualKey.Application ||
                e.Key == global::Windows.System.VirtualKey.F10 && shift))
                e.Handled = Show(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(surface.XamlRoot) as DependencyObject, null);
        };
    }

    internal static MenuFlyout Create(IEnumerable<Action> actions)
    {
        var menu = new MenuFlyout();
        foreach (var action in actions)
        {
            var item = new MenuFlyoutItem { Text = action.Label, IsEnabled = action.Enabled };
            item.Click += (_, _) => action.Invoke();
            menu.Items.Add(item);
        }
        return menu;
    }

    internal static IEnumerable<Action> FromButtons(DependencyObject root)
    {
        if (root is UIElement { Visibility: Visibility.Collapsed }) yield break;
        if (root is Button button)
        {
            var label = button.Content as string ?? Texts(button.Content as DependencyObject).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(label))
                yield return new(label, () =>
                {
                    if (!button.IsEnabled || button.Visibility != Visibility.Visible) return;
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
                    (peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
                }, button.IsEnabled);
            yield break;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var action in FromButtons(VisualTreeHelper.GetChild(root, index))) yield return action;
    }

    private static IEnumerable<string> Texts(DependencyObject? root)
    {
        if (root is null) yield break;
        if (root is TextBlock text) { yield return text.Text; yield break; }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var value in Texts(VisualTreeHelper.GetChild(root, index))) yield return value;
    }
}
