using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

public sealed class DragHandleGrid : Grid
{
    private readonly InputSystemCursor _moveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private readonly InputSystemCursor _arrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    internal void SetDraggable(bool draggable) => ProtectedCursor = draggable ? _moveCursor : _arrowCursor;
}

public sealed class ResizeHandleGrid : Grid
{
    internal ResizeHandleGrid(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}
