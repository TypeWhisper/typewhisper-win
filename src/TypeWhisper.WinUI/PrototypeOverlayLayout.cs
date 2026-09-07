namespace TypeWhisper.WinUI;

internal enum PrototypeOverlayMode { Standard, Compact, Minimal }
internal enum PrototypeOverlayAnchor { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }
internal enum PrototypeOverlayWidget { None, Indicator, Waveform, Timer, Clock, Profile, HotkeyMode, AppName }

internal sealed record PrototypeOverlayPreferences(PrototypeOverlayMode Mode, bool LiveText, bool TechnicalDetails,
    PrototypeOverlayAnchor Anchor = PrototypeOverlayAnchor.BottomCenter,
    PrototypeOverlayWidget Left = PrototypeOverlayWidget.Waveform,
    PrototypeOverlayWidget Right = PrototypeOverlayWidget.Timer,
    double LiveTranscriptionFontSize = TypeWhisper.Core.Models.AppSettings.DefaultLiveTranscriptionFontSize,
    int PreviewBubbleAutoHideMilliseconds = TypeWhisper.Core.Models.AppSettings.DefaultPreviewBubbleAutoHideMilliseconds)
{
    internal bool IsValid => Enum.IsDefined(Mode) && Enum.IsDefined(Anchor) && Enum.IsDefined(Left) && Enum.IsDefined(Right)
        && double.IsFinite(LiveTranscriptionFontSize) && LiveTranscriptionFontSize is >= 10 and <= 18
        && PreviewBubbleAutoHideMilliseconds is >= 0 and <= 5000;
    internal bool AtTop => Anchor is PrototypeOverlayAnchor.TopLeft or PrototypeOverlayAnchor.TopCenter or PrototypeOverlayAnchor.TopRight;
    internal int HorizontalIndex => Anchor switch
    {
        PrototypeOverlayAnchor.TopLeft or PrototypeOverlayAnchor.BottomLeft => 0,
        PrototypeOverlayAnchor.TopRight or PrototypeOverlayAnchor.BottomRight => 2,
        _ => 1
    };
    internal static PrototypeOverlayAnchor Snap(double x, double y) =>
        (PrototypeOverlayAnchor)((y < 0.5 ? 0 : 3) + (x < 0.25 ? 0 : x > 0.75 ? 2 : 1));

    internal PrototypeOverlayPreferences SelectWidget(bool left, PrototypeOverlayWidget widget)
    {
        if (widget != PrototypeOverlayWidget.None && widget == (left ? Right : Left))
            return this with { Left = Right, Right = Left };
        return left ? this with { Left = widget } : this with { Right = widget };
    }
}
