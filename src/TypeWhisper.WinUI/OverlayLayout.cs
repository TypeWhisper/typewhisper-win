namespace TypeWhisper.WinUI;

internal enum OverlayScreen { ActiveScreen, PrimaryScreen }
internal enum OverlayMode { Standard, Compact, Minimal }
internal enum OverlayAnchor { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }
internal enum OverlayWidget { None, Indicator, Waveform, Timer, Clock, Profile, HotkeyMode, AppName }

internal sealed record OverlayPreferences(OverlayMode Mode, bool LiveText, bool TechnicalDetails,
    OverlayAnchor Anchor = OverlayAnchor.BottomCenter,
    OverlayWidget Left = OverlayWidget.Waveform,
    OverlayWidget Right = OverlayWidget.Timer,
    double LiveTranscriptionFontSize = TypeWhisper.Core.Models.AppSettings.DefaultLiveTranscriptionFontSize,
    int PreviewBubbleAutoHideMilliseconds = TypeWhisper.Core.Models.AppSettings.DefaultPreviewBubbleAutoHideMilliseconds,
    OverlayScreen Screen = OverlayScreen.ActiveScreen)
{
    internal bool IsValid => Enum.IsDefined(Screen) && Enum.IsDefined(Mode) && Enum.IsDefined(Anchor) && Enum.IsDefined(Left) && Enum.IsDefined(Right)
        && double.IsFinite(LiveTranscriptionFontSize) && LiveTranscriptionFontSize is >= 10 and <= 18
        && PreviewBubbleAutoHideMilliseconds is >= 0 and <= 5000;
    internal bool AtTop => Anchor is OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight;
    internal int HorizontalIndex => Anchor switch
    {
        OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft => 0,
        OverlayAnchor.TopRight or OverlayAnchor.BottomRight => 2,
        _ => 1
    };
    internal static OverlayAnchor Snap(double x, double y) =>
        (OverlayAnchor)((y < 0.5 ? 0 : 3) + (x < 0.25 ? 0 : x > 0.75 ? 2 : 1));

    internal OverlayPreferences SelectWidget(bool left, OverlayWidget widget)
    {
        if (widget != OverlayWidget.None && widget == (left ? Right : Left))
            return this with { Left = Right, Right = Left };
        return left ? this with { Left = widget } : this with { Right = widget };
    }
}
