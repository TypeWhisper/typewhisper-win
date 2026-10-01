using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace TypeWhisper.WinUI;

public sealed partial class OverlayEditor : UserControl
{
    private OverlayPreferences _preferences = new(OverlayMode.Standard, true, false);
    private bool _dragging;
    private bool _dragLeft;
    private bool _slotDragging;
    internal event Action<OverlayPreferences>? Changed;
    private static readonly Choice[] Widgets = Enum.GetValues<OverlayWidget>()
        .Select(widget => new Choice(widget.ToString(), Label(widget), widget switch
        {
            OverlayWidget.Profile => Loc.T("Sample profile in this preview"),
            OverlayWidget.AppName => Loc.T("Sample app name in this preview"),
            OverlayWidget.HotkeyMode => Loc.T("Sample recording mode in this preview"),
            OverlayWidget.None => Loc.T("Leave this slot empty"),
            _ => Loc.T("Shown in Standard and Compact")
        })).ToArray();

    public OverlayEditor()
    {
        InitializeComponent();
        EditorTitle.Text = Loc.T("Overlay editor");
        EditorDescription.Text = Loc.T("Drag the preview to a screen edge. Arrange the two content slots below.");
        ScreenPreviewLabel.Text = Loc.T("SCREEN PREVIEW");
        ContentHeading.Text = Loc.T("OVERLAY CONTENT");
        LeftSlotHint.Text = Loc.T("LEFT  ·  DRAG TO MOVE");
        RightSlotHint.Text = Loc.T("RIGHT  ·  DRAG TO MOVE");
        SwapButton.Content = Loc.T("Swap left and right");
        AutomationProperties.SetName(SwapButton, Loc.T("Swap overlay widgets"));
        ResetButton.Content = Loc.T("Reset layout");
        AutomationProperties.SetName(ResetButton, Loc.T("Reset overlay layout"));
        ScreenPicker.Configure(Loc.T("Display"), "desktop", Loc.T("Overlay display"));
        ScreenPicker.SelectionChanged += value => Publish(_preferences with { Screen = Enum.Parse<OverlayScreen>(value) });
        EdgePicker.Configure(Loc.T("Screen edge"), "desktop", Loc.T("Overlay screen edge"));
        AlignmentPicker.Configure(Loc.T("Alignment"), "layout", Loc.T("Overlay screen alignment"));
        LeftWidgetPicker.Configure(Loc.T("Left widget"), "workflow", Loc.T("Overlay left widget"));
        RightWidgetPicker.Configure(Loc.T("Right widget"), "workflow", Loc.T("Overlay right widget"));
        EdgePicker.SelectionChanged += value => Publish(_preferences with { Anchor = (OverlayAnchor)((value == "top" ? 0 : 3) + _preferences.HorizontalIndex) });
        AlignmentPicker.SelectionChanged += value => Publish(_preferences with { Anchor = (OverlayAnchor)((_preferences.AtTop ? 0 : 3) + int.Parse(value)) });
        LeftWidgetPicker.SelectionChanged += value => Publish(_preferences.SelectWidget(true, Enum.Parse<OverlayWidget>(value)));
        RightWidgetPicker.SelectionChanged += value => Publish(_preferences.SelectWidget(false, Enum.Parse<OverlayWidget>(value)));
        SetPreferences(_preferences);
    }

    private static string Label(OverlayWidget widget) => widget switch
    {
        OverlayWidget.HotkeyMode => Loc.T("Hotkey mode"),
        OverlayWidget.AppName => Loc.T("App name"),
        OverlayWidget.None => Loc.T("None"),
        OverlayWidget.Indicator => Loc.T("Indicator"),
        OverlayWidget.Waveform => Loc.T("Waveform"),
        OverlayWidget.Timer => Loc.T("Timer"),
        OverlayWidget.Clock => Loc.T("Clock"),
        OverlayWidget.Profile => Loc.T("Profile"),
        _ => widget.ToString()
    };

    internal void SetPreferences(OverlayPreferences preferences)
    {
        _preferences = preferences;
        ScreenPicker.SetOptions([new("ActiveScreen", Loc.T("Active screen"), Loc.T("Follow the screen containing the active window")),
            new("PrimaryScreen", Loc.T("Primary screen"), Loc.T("Always use your main Windows display"))], preferences.Screen.ToString());
        EdgePicker.SetOptions([new("top", Loc.T("Top"), Loc.T("Live text opens downward")), new("bottom", Loc.T("Bottom"), Loc.T("Live text opens upward"))], preferences.AtTop ? "top" : "bottom");
        AlignmentPicker.SetOptions([new("0", Loc.T("Left"), Loc.T("Align to the left edge")), new("1", Loc.T("Center"), Loc.T("Keep centered")), new("2", Loc.T("Right"), Loc.T("Align to the right edge"))], preferences.HorizontalIndex.ToString());
        LeftWidgetPicker.SetOptions(Widgets, preferences.Left.ToString());
        RightWidgetPicker.SetOptions(Widgets, preferences.Right.ToString());
        LeftLabel.Text = Label(preferences.Left);
        RightLabel.Text = Label(preferences.Right);
        var minimal = preferences.Mode == OverlayMode.Minimal;
        WidgetSlots.IsHitTestVisible = !minimal;
        WidgetSlots.Opacity = minimal ? 0.45 : 1;
        LeftWidgetPicker.IsEnabled = RightWidgetPicker.IsEnabled = SwapButton.IsEnabled = !minimal;
        ContentHint.Text = minimal ? Loc.T("Minimal keeps only its indicator. Your left and right widgets are remembered for the other layouts.")
            : Loc.T("Drag a slot across to swap it, or choose a widget. The recording indicator stays visible.");
        ThumbText.Text = minimal ? "● ━━━━━" : $"{ShortLabel(preferences.Left)}  ·  {ShortLabel(preferences.Right)}";
        PositionSummary.Text = Loc.T("{0} · {1} · snaps to six screen positions", preferences.AtTop ? Loc.T("Top") : Loc.T("Bottom"), new[] { Loc.T("Left"), Loc.T("Center"), Loc.T("Right") }[preferences.HorizontalIndex]);
        PositionThumb();
    }

    private static string ShortLabel(OverlayWidget widget) => widget switch
    {
        OverlayWidget.Waveform => "┃┃┃┃",
        OverlayWidget.Timer => "00:12",
        OverlayWidget.None => "—",
        OverlayWidget.Indicator => "●",
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
        Publish(_preferences with { Anchor = OverlayPreferences.Snap(point.X / Math.Max(1, ScreenCanvas.ActualWidth), point.Y / ScreenCanvas.Height) });
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
    private void Publish(OverlayPreferences preferences) { SetPreferences(preferences); Changed?.Invoke(preferences); }
    private void Swap() => Publish(_preferences with { Left = _preferences.Right, Right = _preferences.Left });
    private void Swap_Click(object sender, RoutedEventArgs e) => Swap();
    private void Reset_Click(object sender, RoutedEventArgs e) => Publish(_preferences with
    {
        Screen = OverlayScreen.ActiveScreen, Anchor = OverlayAnchor.BottomCenter, Left = OverlayWidget.Waveform, Right = OverlayWidget.Timer
    });
    internal bool CloseOpenPicker()
    {
        foreach (var picker in new[] { ScreenPicker, EdgePicker, AlignmentPicker, LeftWidgetPicker, RightWidgetPicker })
            if (picker.IsPopupOpen) { picker.ClosePopup(); return true; }
        return false;
    }
}
