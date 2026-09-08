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
        surface.ContextRequested += (_, e) =>
        {
            if (e.Handled) return;
            var source = e.OriginalSource as DependencyObject;
            ListViewItem? row = null;
            for (var node = source; node is not null && node != surface; node = VisualTreeHelper.GetParent(node))
            {
                if (node is TextBox or PasswordBox or RichEditBox ||
                    node is TextBlock { IsTextSelectionEnabled: true }) return;
                if (node is FrameworkElement { ContextFlyout: not null }) return;
                if (node is ListViewItem item) { row = item; break; }
            }
            if (row is not null && ItemsControl.ItemsControlFromItemContainer(row) is ListView list)
            {
                if (!row.IsSelected)
                {
                    list.SelectedItems.Clear();
                    row.IsSelected = true;
                }
                row.Focus(FocusState.Programmatic);
            }
            var menu = Create(actions());
            if (menu.Items.Count == 0) return;
            e.Handled = true;
            if (e.TryGetPosition(surface, out var point))
                menu.ShowAt(surface, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
            else menu.ShowAt(row ?? surface);
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
