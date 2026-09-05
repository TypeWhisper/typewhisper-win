using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace TypeWhisper.WinUIPrototype;

/// <summary>
/// Small source-owned icon set for the prototype's signal-oriented visual language.
/// </summary>
public sealed class TypeWhisperGlyph : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(string),
        typeof(TypeWhisperGlyph),
        new PropertyMetadata("signal", OnKindChanged));

    private static readonly Color Accent = Color.FromArgb(255, 59, 167, 255);
    private readonly CanvasControl _canvas;

    public TypeWhisperGlyph()
    {
        IsHitTestVisible = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _canvas = new CanvasControl { ClearColor = Color.FromArgb(0, 0, 0, 0) };
        _canvas.Draw += Canvas_Draw;
        Content = _canvas;
    }

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is TypeWhisperGlyph glyph)
            glyph._canvas.Invalidate();
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var size = sender.Size;
        if (size.Width <= 1 || size.Height <= 1)
            return;

        var scale = (float)Math.Min(size.Width, size.Height) / 20f;
        var offsetX = ((float)size.Width - 20 * scale) / 2;
        var offsetY = ((float)size.Height - 20 * scale) / 2;
        var drawing = args.DrawingSession;
        drawing.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offsetX, offsetY);

        switch (Kind)
        {
            case "home":
                Line(drawing, 2, 9, 10, 2); Line(drawing, 10, 2, 18, 9);
                Line(drawing, 4, 8, 4, 18); Line(drawing, 4, 18, 16, 18); Line(drawing, 16, 18, 16, 8);
                drawing.DrawRectangle(8, 12, 4, 6, Accent, 1.4f); break;
            case "stats":
                drawing.FillRoundedRectangle(3, 10, 3, 7, 1, 1, Accent);
                drawing.FillRoundedRectangle(8, 3, 3, 14, 1, 1, Accent);
                drawing.FillRoundedRectangle(13, 7, 3, 10, 1, 1, Accent); Line(drawing, 2, 19, 18, 19); break;
            case "calendar":
                drawing.DrawRoundedRectangle(3, 4, 14, 14, 2, 2, Accent, 1.4f); Line(drawing, 3, 8, 17, 8);
                Line(drawing, 6, 2, 6, 5); Line(drawing, 14, 2, 14, 5);
                for (var r = 0; r < 2; r++) for (var c = 0; c < 3; c++) drawing.FillCircle(6 + c * 4, 11 + r * 4, .7f, Accent); break;
            case "speed":
                drawing.DrawCircle(10, 10, 8, Accent, 1.4f); Line(drawing, 10, 10, 14, 6);
                drawing.FillCircle(10, 10, 1.2f, Accent); Line(drawing, 5, 10, 6, 10); Line(drawing, 10, 4, 10, 5); Line(drawing, 14, 12, 15, 13); break;
            case "trophy":
                drawing.DrawRoundedRectangle(6, 2, 8, 10, 3, 3, Accent, 1.4f); Line(drawing, 10, 12, 10, 17); Line(drawing, 6, 18, 14, 18);
                Line(drawing, 6, 4, 2, 4); Line(drawing, 2, 4, 3, 8); Line(drawing, 3, 8, 6, 10);
                Line(drawing, 14, 4, 18, 4); Line(drawing, 18, 4, 17, 8); Line(drawing, 17, 8, 14, 10); break;
            case "flame":
                using (var flame = new CanvasPathBuilder(drawing))
                {
                    flame.BeginFigure(10, 2); flame.AddCubicBezier(new(19, 9), new(17, 18), new(10, 18));
                    flame.AddCubicBezier(new(2, 18), new(3, 10), new(6, 7)); flame.AddLine(8, 11);
                    flame.AddCubicBezier(new(11, 8), new(12, 5), new(10, 2)); flame.EndFigure(CanvasFigureLoop.Closed);
                    using var geometry = CanvasGeometry.CreatePath(flame); drawing.DrawGeometry(geometry, Accent, 1.4f);
                }
                break;
            case "folder":
                Line(drawing, 2, 5, 8, 5); Line(drawing, 8, 5, 10, 7);
                Line(drawing, 10, 7, 18, 7); Line(drawing, 18, 7, 18, 16);
                Line(drawing, 18, 16, 2, 16); Line(drawing, 2, 16, 2, 5);
                Line(drawing, 2, 9, 18, 9);
                break;
            case "restore":
                var restoreColor = (Foreground as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color ?? Accent;
                using (var path = new CanvasPathBuilder(drawing))
                {
                    path.BeginFigure(4, 6);
                    path.AddCubicBezier(new Vector2(8, 0), new Vector2(18, 4), new Vector2(17, 11));
                    path.AddCubicBezier(new Vector2(16, 18), new Vector2(6, 19), new Vector2(3, 13));
                    path.EndFigure(CanvasFigureLoop.Open);
                    using var geometry = CanvasGeometry.CreatePath(path);
                    using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
                    drawing.DrawGeometry(geometry, restoreColor, 1.5f, stroke);
                }
                drawing.DrawLine(4, 2, 4, 6, restoreColor, 1.5f);
                drawing.DrawLine(4, 6, 8, 6, restoreColor, 1.5f);
                break;
            case "keyboard":
                drawing.DrawRoundedRectangle(2, 4, 16, 12, 2, 2, Accent, 1.4f);
                for (var row = 0; row < 2; row++)
                    for (var col = 0; col < 4; col++) drawing.FillCircle(5 + col * 3.3f, 7 + row * 3, .65f, Accent);
                Line(drawing, 6, 13, 14, 13);
                break;
            case "chip":
                drawing.DrawRoundedRectangle(5, 5, 10, 10, 1.5f, 1.5f, Accent, 1.4f);
                drawing.DrawRectangle(8, 8, 4, 4, Accent, 1.2f);
                for (var pin = 0; pin < 3; pin++)
                {
                    var p = 7 + pin * 3;
                    Line(drawing, p, 2, p, 4); Line(drawing, p, 16, p, 18);
                    Line(drawing, 2, p, 4, p); Line(drawing, 16, p, 18, p);
                }
                break;
            case "layout":
                drawing.DrawRoundedRectangle(2, 3, 16, 14, 2, 2, Accent, 1.4f);
                Line(drawing, 2, 7, 18, 7); Line(drawing, 9, 7, 9, 17);
                break;
            case "text":
                Line(drawing, 3, 5, 17, 5); Line(drawing, 3, 10, 17, 10); Line(drawing, 3, 15, 12, 15);
                break;
            case "lock":
                drawing.DrawRoundedRectangle(4, 9, 12, 9, 2, 2, Accent, 1.4f);
                drawing.DrawRoundedRectangle(6.5f, 2, 7, 10, 3.5f, 3.5f, Accent, 1.4f);
                drawing.FillRectangle(5, 10, 10, 6, Windows.UI.Color.FromArgb(255, 17, 25, 35));
                drawing.FillCircle(10, 13, 1.2f, Accent);
                break;
            case "info":
                drawing.DrawCircle(10, 10, 7.5f, Accent, 1.4f);
                drawing.FillCircle(10, 6, 1, Accent); Line(drawing, 10, 9, 10, 14);
                break;
            case "desktop":
                drawing.DrawRoundedRectangle(2.5f, 3, 15, 10, 1.5f, 1.5f, Accent, 1.4f);
                Line(drawing, 10, 13, 10, 17);
                Line(drawing, 6, 17, 14, 17);
                break;
            case "laptop":
                drawing.DrawRoundedRectangle(4, 3.5f, 12, 10, 1, 1, Accent, 1.4f);
                Line(drawing, 4, 13.5f, 2, 16.5f);
                Line(drawing, 2, 16.5f, 18, 16.5f);
                Line(drawing, 18, 16.5f, 16, 13.5f);
                break;
            case "phone":
                drawing.DrawRoundedRectangle(5.5f, 2, 9, 16, 2, 2, Accent, 1.4f);
                Line(drawing, 8.5f, 4, 11.5f, 4);
                Line(drawing, 8.5f, 15.5f, 11.5f, 15.5f);
                break;
            case "devices":
                drawing.DrawRoundedRectangle(2, 3, 12, 9, 1, 1, Accent, 1.4f);
                Line(drawing, 7, 12, 7, 16);
                Line(drawing, 4, 16, 10, 16);
                drawing.FillRoundedRectangle(11, 7, 8, 12, 1.5f, 1.5f, Windows.UI.Color.FromArgb(255, 17, 25, 35));
                drawing.DrawRoundedRectangle(12, 8, 6, 10, 1, 1, Accent, 1.4f);
                break;
            case "check":
                Line(drawing, 4, 10, 8, 14);
                Line(drawing, 8, 14, 16, 6);
                break;
            case "chevron-down":
                Line(drawing, 5, 8, 10, 13);
                Line(drawing, 10, 13, 15, 8);
                break;
            case "microphone":
                drawing.DrawRoundedRectangle(7, 3, 6, 10, 3, 3, Accent, 1.5f);
                Line(drawing, 5, 9, 5, 11);
                Line(drawing, 5, 11, 7, 14);
                Line(drawing, 7, 14, 10, 15);
                Line(drawing, 10, 15, 13, 14);
                Line(drawing, 13, 14, 15, 11);
                Line(drawing, 15, 11, 15, 9);
                Line(drawing, 10, 15, 10, 18);
                Line(drawing, 7, 18, 13, 18);
                break;
            case "speaker":
                Line(drawing, 3, 7, 6, 7);
                Line(drawing, 6, 7, 10, 3.5f);
                Line(drawing, 10, 3.5f, 10, 16.5f);
                Line(drawing, 10, 16.5f, 6, 13);
                Line(drawing, 6, 13, 3, 13);
                Line(drawing, 3, 13, 3, 7);
                Line(drawing, 13, 7, 14.5f, 10);
                Line(drawing, 14.5f, 10, 13, 13);
                Line(drawing, 15.5f, 4.5f, 18, 10);
                Line(drawing, 18, 10, 15.5f, 15.5f);
                break;
            case "pause":
                drawing.FillRoundedRectangle(5.5f, 4, 3, 12, 1.5f, 1.5f, Accent);
                drawing.FillRoundedRectangle(11.5f, 4, 3, 12, 1.5f, 1.5f, Accent);
                break;
            case "history":
                drawing.DrawCircle(10, 10, 6.5f, Accent, 1.4f);
                Line(drawing, 10, 6.5f, 10, 10);
                Line(drawing, 10, 10, 13, 11.5f);
                Line(drawing, 3.1f, 5.1f, 3.1f, 9);
                Line(drawing, 3.1f, 5.1f, 7, 5.1f);
                break;
            case "recorder":
                Line(drawing, 8, 5, 8, 14);
                Line(drawing, 8, 5, 15, 3.5f);
                Line(drawing, 15, 3.5f, 15, 12);
                drawing.FillCircle(5.8f, 15.2f, 2.2f, Accent);
                drawing.FillCircle(12.8f, 13.2f, 2.2f, Accent);
                break;
            case "workflow":
                drawing.FillRoundedRectangle(3, 7, 2.2f, 6, 1.1f, 1.1f, Accent);
                drawing.FillRoundedRectangle(8.9f, 3, 2.2f, 14, 1.1f, 1.1f, Accent);
                drawing.FillRoundedRectangle(14.8f, 5, 2.2f, 10, 1.1f, 1.1f, Accent);
                break;
            case "plugin":
                Line(drawing, 8, 3, 6, 5);
                Line(drawing, 6, 5, 6, 8);
                Line(drawing, 6, 8, 3.5f, 10);
                Line(drawing, 3.5f, 10, 6, 12);
                Line(drawing, 6, 12, 6, 15);
                Line(drawing, 6, 15, 8, 17);
                Line(drawing, 12, 3, 14, 5);
                Line(drawing, 14, 5, 14, 8);
                Line(drawing, 14, 8, 16.5f, 10);
                Line(drawing, 16.5f, 10, 14, 12);
                Line(drawing, 14, 12, 14, 15);
                Line(drawing, 14, 15, 12, 17);
                break;
            case "market":
                drawing.DrawRoundedRectangle(4, 6.5f, 12, 10.5f, 2, 2, Accent, 1.4f);
                Line(drawing, 7, 6.5f, 7, 5);
                Line(drawing, 7, 5, 8.5f, 3.5f);
                Line(drawing, 8.5f, 3.5f, 11.5f, 3.5f);
                Line(drawing, 11.5f, 3.5f, 13, 5);
                Line(drawing, 13, 5, 13, 6.5f);
                Line(drawing, 7, 9, 7, 11);
                Line(drawing, 13, 9, 13, 11);
                break;
            case "settings":
                Line(drawing, 3, 5, 17, 5);
                Line(drawing, 3, 10, 17, 10);
                Line(drawing, 3, 15, 17, 15);
                drawing.FillCircle(7, 5, 2, Accent);
                drawing.FillCircle(13, 10, 2, Accent);
                drawing.FillCircle(9, 15, 2, Accent);
                break;
            case "file":
                Line(drawing, 5, 2.5f, 12, 2.5f);
                Line(drawing, 12, 2.5f, 16, 6.5f);
                Line(drawing, 16, 6.5f, 16, 17.5f);
                Line(drawing, 16, 17.5f, 5, 17.5f);
                Line(drawing, 5, 17.5f, 5, 2.5f);
                Line(drawing, 12, 2.5f, 12, 6.5f);
                Line(drawing, 12, 6.5f, 16, 6.5f);
                Line(drawing, 8, 11, 13, 11);
                Line(drawing, 8, 14, 12, 14);
                break;
            case "dictionary":
                using (var format = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI Variable Display",
                    FontSize = 9.5f,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center
                })
                {
                    drawing.DrawText("Aa", 0, 0, 20, 20, Accent, format);
                }
                break;
            case "run":
                drawing.FillRoundedRectangle(3, 8.7f, 3, 2.6f, 1.3f, 1.3f, Accent);
                drawing.FillRoundedRectangle(8.5f, 6, 3, 8, 1.5f, 1.5f, Accent);
                drawing.FillRoundedRectangle(14, 3.5f, 3, 13, 1.5f, 1.5f, Accent);
                break;
            case "actions":
                drawing.FillCircle(5, 10, 1.5f, Accent);
                drawing.FillCircle(10, 10, 1.5f, Accent);
                drawing.FillCircle(15, 10, 1.5f, Accent);
                break;
            case "pin":
                Line(drawing, 7, 3, 15.5f, 11.5f);
                Line(drawing, 11.5f, 3.5f, 15, 7);
                Line(drawing, 15, 7, 12, 10);
                Line(drawing, 12, 10, 13.5f, 13.5f);
                Line(drawing, 13.5f, 13.5f, 6.5f, 6.5f);
                Line(drawing, 6.5f, 6.5f, 10, 8);
                Line(drawing, 10, 8, 3, 17);
                break;
            case "search":
                drawing.DrawCircle(8.5f, 8.5f, 5, Accent, 1.5f);
                Line(drawing, 12.2f, 12.2f, 17, 17);
                break;
            default:
                var heights = new[] { 6f, 11f, 16f, 11f, 6f };
                for (var index = 0; index < heights.Length; index++)
                    drawing.FillRoundedRectangle(3 + index * 3.5f, 10 - heights[index] / 2, 2, heights[index], 1, 1, Accent);
                break;
        }
    }

    private static void Line(CanvasDrawingSession drawing, float x1, float y1, float x2, float y2)
    {
        const float width = 1.5f;
        drawing.DrawLine(x1, y1, x2, y2, Accent, width);
        drawing.FillCircle(x1, y1, width / 2, Accent);
        drawing.FillCircle(x2, y2, width / 2, Accent);
    }
}
