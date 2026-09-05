using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace TypeWhisper.WinUIPrototype;

public sealed partial class PrototypeOverlayEditor : UserControl
{
    private PrototypeOverlayPreferences _preferences = new(PrototypeOverlayMode.Standard, true, false);
    private bool _dragging;
    private bool _dragLeft;
    private bool _slotDragging;
    internal event Action<PrototypeOverlayPreferences>? Changed;
    private static readonly PrototypeChoice[] Widgets = Enum.GetValues<PrototypeOverlayWidget>()
        .Select(widget => new PrototypeChoice(widget.ToString(), Label(widget), widget switch
        {
            PrototypeOverlayWidget.Profile => "Sample profile in this prototype",
            PrototypeOverlayWidget.AppName => "Sample app name in this prototype",
            PrototypeOverlayWidget.HotkeyMode => "Sample recording mode in this prototype",
            PrototypeOverlayWidget.None => "Leave this slot empty",
            _ => "Shown in Standard and Compact"
        })).ToArray();

    public PrototypeOverlayEditor()
    {
        InitializeComponent();
        EdgePicker.Configure("Screen edge", "desktop", "Overlay screen edge");
        AlignmentPicker.Configure("Alignment", "layout", "Overlay screen alignment");
        LeftWidgetPicker.Configure("Left widget", "workflow", "Overlay left widget");
        RightWidgetPicker.Configure("Right widget", "workflow", "Overlay right widget");
        EdgePicker.SelectionChanged += value => Publish(_preferences with { Anchor = (PrototypeOverlayAnchor)((value == "top" ? 0 : 3) + _preferences.HorizontalIndex) });
        AlignmentPicker.SelectionChanged += value => Publish(_preferences with { Anchor = (PrototypeOverlayAnchor)((_preferences.AtTop ? 0 : 3) + int.Parse(value)) });
        LeftWidgetPicker.SelectionChanged += value => Publish(_preferences.SelectWidget(true, Enum.Parse<PrototypeOverlayWidget>(value)));
        RightWidgetPicker.SelectionChanged += value => Publish(_preferences.SelectWidget(false, Enum.Parse<PrototypeOverlayWidget>(value)));
        SetPreferences(_preferences);
    }

    private static string Label(PrototypeOverlayWidget widget) => widget switch
    {
        PrototypeOverlayWidget.HotkeyMode => "Hotkey mode",
        PrototypeOverlayWidget.AppName => "App name",
        _ => widget.ToString()
    };

    internal void SetPreferences(PrototypeOverlayPreferences preferences)
    {
        _preferences = preferences;
        EdgePicker.SetOptions([new("top", "Top", "Live text opens downward"), new("bottom", "Bottom", "Live text opens upward")], preferences.AtTop ? "top" : "bottom");
        AlignmentPicker.SetOptions([new("0", "Left", "Align to the left edge"), new("1", "Center", "Keep centered"), new("2", "Right", "Align to the right edge")], preferences.HorizontalIndex.ToString());
        LeftWidgetPicker.SetOptions(Widgets, preferences.Left.ToString());
        RightWidgetPicker.SetOptions(Widgets, preferences.Right.ToString());
        LeftLabel.Text = Label(preferences.Left);
        RightLabel.Text = Label(preferences.Right);
        var minimal = preferences.Mode == PrototypeOverlayMode.Minimal;
        WidgetSlots.IsHitTestVisible = !minimal;
        WidgetSlots.Opacity = minimal ? 0.45 : 1;
        LeftWidgetPicker.IsEnabled = RightWidgetPicker.IsEnabled = SwapButton.IsEnabled = !minimal;
        ContentHint.Text = minimal ? "Minimal keeps only its indicator. Your left and right widgets are remembered for the other layouts."
            : "Drag a slot across to swap it, or choose a widget. The recording indicator stays visible.";
        ThumbText.Text = minimal ? "● ━━━━━" : $"{ShortLabel(preferences.Left)}  ·  {ShortLabel(preferences.Right)}";
        PositionSummary.Text = $"{(preferences.AtTop ? "Top" : "Bottom")} · {new[] { "Left", "Center", "Right" }[preferences.HorizontalIndex]} · snaps to six screen positions";
        PositionThumb();
    }

    private static string ShortLabel(PrototypeOverlayWidget widget) => widget switch
    {
        PrototypeOverlayWidget.Waveform => "┃┃┃┃",
        PrototypeOverlayWidget.Timer => "00:12",
        PrototypeOverlayWidget.None => "—",
        PrototypeOverlayWidget.Indicator => "●",
        _ => Label(widget)
    };

    private void PositionThumb()
    {
        if (_dragging) return;
        Canvas.SetLeft(OverlayThumb, Math.Max(0, ScreenCanvas.ActualWidth - OverlayThumb.Width) * _preferences.HorizontalIndex / 2);
        Canvas.SetTop(OverlayThumb, _preferences.AtTop ? 0 : ScreenCanvas.Height - OverlayThumb.Height);
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => PositionThumb();
    private void Thumb_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(OverlayThumb).Properties.IsLeftButtonPressed) return;
        _dragging = OverlayThumb.CapturePointer(e.Pointer);
        e.Handled = _dragging;
    }
    private void Thumb_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetCurrentPoint(ScreenCanvas).Position;
        Canvas.SetLeft(OverlayThumb, Math.Clamp(point.X - OverlayThumb.Width / 2, 0, Math.Max(0, ScreenCanvas.ActualWidth - OverlayThumb.Width)));
        Canvas.SetTop(OverlayThumb, Math.Clamp(point.Y - OverlayThumb.Height / 2, 0, ScreenCanvas.Height - OverlayThumb.Height));
        e.Handled = true;
    }
    private void Thumb_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetCurrentPoint(ScreenCanvas).Position;
        _dragging = false;
        OverlayThumb.ReleasePointerCapture(e.Pointer);
        Publish(_preferences with { Anchor = PrototypeOverlayPreferences.Snap(point.X / Math.Max(1, ScreenCanvas.ActualWidth), point.Y / ScreenCanvas.Height) });
        e.Handled = true;
    }
    private void Thumb_CaptureLost(object sender, PointerRoutedEventArgs e) { _dragging = false; PositionThumb(); }
    private void Slot_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) return;
        _dragLeft = ReferenceEquals(sender, LeftSlot);
        _slotDragging = ((UIElement)sender).CapturePointer(e.Pointer);
        ((UIElement)sender).Opacity = 0.65;
        e.Handled = true;
    }
    private void Slot_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_slotDragging) return;
        var point = e.GetCurrentPoint(WidgetSlots).Position;
        var otherSide = _dragLeft ? point.X > WidgetSlots.ActualWidth / 2 : point.X < WidgetSlots.ActualWidth / 2;
        var inBounds = point.X >= 0 && point.X <= WidgetSlots.ActualWidth && point.Y >= 0 && point.Y <= WidgetSlots.ActualHeight;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _slotDragging = false;
        if (inBounds && otherSide) Swap();
        e.Handled = true;
    }
    private void Slot_CaptureLost(object sender, PointerRoutedEventArgs e) { _slotDragging = false; ((UIElement)sender).Opacity = 1; }
    private void Publish(PrototypeOverlayPreferences preferences) { SetPreferences(preferences); Changed?.Invoke(preferences); }
    private void Swap() => Publish(_preferences with { Left = _preferences.Right, Right = _preferences.Left });
    private void Swap_Click(object sender, RoutedEventArgs e) => Swap();
    private void Reset_Click(object sender, RoutedEventArgs e) => Publish(_preferences with
    {
        Anchor = PrototypeOverlayAnchor.BottomCenter, Left = PrototypeOverlayWidget.Waveform, Right = PrototypeOverlayWidget.Timer
    });
    internal bool CloseOpenPicker()
    {
        foreach (var picker in new[] { EdgePicker, AlignmentPicker, LeftWidgetPicker, RightWidgetPicker })
            if (picker.IsPopupOpen) { picker.ClosePopup(); return true; }
        return false;
    }
}
