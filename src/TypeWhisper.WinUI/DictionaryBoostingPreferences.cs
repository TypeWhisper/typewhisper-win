using System.Text.Json;
using TypeWhisper.Core.Services;

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
            AtomicFileWriter.WriteAllText(PathName, JsonSerializer.Serialize(new Options(enabled)));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Loc.T("Could not save vocabulary boosting preference."); }
    }
    private sealed record Options(bool Enabled);
}
