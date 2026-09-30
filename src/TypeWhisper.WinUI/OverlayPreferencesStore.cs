using System.Text.Json;
using TypeWhisper.Core.Services;

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
        AtomicFileWriter.WriteAllText(path, JsonSerializer.Serialize(preferences));
    }
}
