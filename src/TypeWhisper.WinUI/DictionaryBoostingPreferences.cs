using System.Text.Json;

namespace TypeWhisper.WinUI;

internal static class DictionaryBoostingPreferences
{
    private static string PathName => Path.Combine(Path.GetDirectoryName(DictationDictionarySnapshot.StoragePath)!, "dictionary-options.json");
    internal static bool Load()
    {
        try { return File.Exists(PathName) && JsonSerializer.Deserialize<Options>(File.ReadAllText(PathName))?.Enabled == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    internal static string? Save(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName + ".tmp", JsonSerializer.Serialize(new Options(enabled)));
            File.Move(PathName + ".tmp", PathName, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "Could not save vocabulary boosting preference."; }
    }
    private sealed record Options(bool Enabled);
}
