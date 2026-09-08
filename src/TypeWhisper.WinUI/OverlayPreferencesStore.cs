using System.Text.Json;

namespace TypeWhisper.WinUI;

internal static class OverlayPreferencesStore
{
    internal static OverlayPreferences Read(string path)
    {
        if (!File.Exists(path)) return new(OverlayMode.Standard, true, false);
        var preferences = JsonSerializer.Deserialize<OverlayPreferences>(File.ReadAllText(path));
        if (preferences?.IsValid != true) throw new JsonException("Invalid overlay preferences.");
        return preferences;
    }

    internal static void Save(string path, OverlayPreferences preferences)
    {
        if (!preferences.IsValid) throw new ArgumentException("Invalid overlay preferences.", nameof(preferences));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".overlay-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
