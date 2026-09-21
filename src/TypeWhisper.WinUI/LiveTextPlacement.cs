using System.Text.Json;

namespace TypeWhisper.WinUI;

internal sealed record LiveTextPosition(int X, int Y, double Width = 420, double Height = 220);
internal readonly record struct LiveTextBounds(int X, int Y, int Width, int Height);
internal sealed record LiveTextPreviewFrame(LiveTextBounds Window, LiveTextBounds WorkArea, double Width = 420, double Height = 220);
internal readonly record struct LiveTextMapRect(double X, double Y, double Width, double Height);
internal readonly record struct LiveTextMapLayout(LiveTextMapRect Screen, LiveTextMapRect Window);
[Flags]
internal enum LiveTextResizeEdge { Left = 1, Right = 2, Top = 4, Bottom = 8 }

internal static class LiveTextPlacement
{
    internal static LiveTextPosition Clamp(LiveTextPosition position, int width, int height,
        int left, int top, int workWidth, int workHeight) => position with
        {
            X = Math.Clamp(position.X, left, left + Math.Max(0, workWidth - width)),
            Y = Math.Clamp(position.Y, top, top + Math.Max(0, workHeight - height))
        };

    internal static LiveTextMapLayout Project(LiveTextPreviewFrame frame, double width, double height)
    {
        var work = frame.WorkArea;
        if (work.Width <= 0 || work.Height <= 0 || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return default;
        var scale = Math.Min(width / work.Width, height / work.Height);
        var x = (width - work.Width * scale) / 2;
        var y = (height - work.Height * scale) / 2;
        return new(new(x, y, work.Width * scale, work.Height * scale),
            new(x + (frame.Window.X - work.X) * scale, y + (frame.Window.Y - work.Y) * scale,
                frame.Window.Width * scale, frame.Window.Height * scale));
    }

    internal const int MinimumWidth = 280;
    internal const int MinimumHeight = 140;

    internal static LiveTextBounds Resize(LiveTextBounds start, LiveTextResizeEdge edge,
        int dx, int dy, int minimumWidth, int minimumHeight, LiveTextBounds work)
    {
        var left = start.X;
        var top = start.Y;
        var right = start.X + start.Width;
        var bottom = start.Y + start.Height;
        minimumWidth = Math.Min(minimumWidth, start.Width);
        minimumHeight = Math.Min(minimumHeight, start.Height);
        if (edge.HasFlag(LiveTextResizeEdge.Left))
            left = (int)Math.Clamp((long)start.X + dx, work.X, right - minimumWidth);
        if (edge.HasFlag(LiveTextResizeEdge.Right))
            right = (int)Math.Clamp((long)right + dx, left + minimumWidth, work.X + work.Width);
        if (edge.HasFlag(LiveTextResizeEdge.Top))
            top = (int)Math.Clamp((long)start.Y + dy, work.Y, bottom - minimumHeight);
        if (edge.HasFlag(LiveTextResizeEdge.Bottom))
            bottom = (int)Math.Clamp((long)bottom + dy, top + minimumHeight, work.Y + work.Height);
        return new(left, top, right - left, bottom - top);
    }

    private static LiveTextPosition Normalize(LiveTextPosition position) => position with
    {
        Width = NormalizeDimension(position.Width, 420, MinimumWidth),
        Height = NormalizeDimension(position.Height, 220, MinimumHeight)
    };

    private static double NormalizeDimension(double value, double fallback, int minimum) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, minimum, 8192) : fallback;

    internal static LiveTextPosition? Read(string path)
    {
        try
        {
            var saved = File.Exists(path) ? JsonSerializer.Deserialize<LiveTextPosition>(File.ReadAllText(path)) : null;
            return saved is null ? null : Normalize(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static void Save(string path, LiveTextPosition position)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(position));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
