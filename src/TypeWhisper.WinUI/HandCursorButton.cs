using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

/// <summary>
/// Gives prototype actions the conventional Windows link/action pointer.
/// </summary>
public sealed class HandCursorButton : Button
{
    public HandCursorButton()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

public sealed class HandCursorListView : ListView
{
    protected override DependencyObject GetContainerForItemOverride() => new HandCursorListViewItem();
}

public sealed class HandCursorToggleButton : Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
{
    public HandCursorToggleButton()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

public sealed class HandCursorListViewItem : ListViewItem
{
    public HandCursorListViewItem()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
