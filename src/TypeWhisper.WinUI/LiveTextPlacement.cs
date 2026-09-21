using System.Text.Json;

namespace TypeWhisper.WinUI;

internal sealed record LiveTextPosition(int X, int Y);

internal static class LiveTextPlacement
{
    internal static LiveTextPosition Clamp(LiveTextPosition position, int width, int height,
        int left, int top, int workWidth, int workHeight) => new(
            Math.Clamp(position.X, left, left + Math.Max(0, workWidth - width)),
            Math.Clamp(position.Y, top, top + Math.Max(0, workHeight - height)));

    internal static LiveTextPosition? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<LiveTextPosition>(File.ReadAllText(path)) : null; }
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
