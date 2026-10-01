using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Loads and atomically saves the chosen interface language in an explicitly supplied profile.</summary>
public sealed class InterfaceLanguageStore
{
    private readonly string _path;
    /// <summary>The saved language code, or null while the app follows the Windows display language.</summary>
    public string? Saved { get; private set; }

    /// <summary>Loads the choice without writing; a missing or invalid file leaves no saved language.</summary>
    public InterfaceLanguageStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Language", out var value) && value.ValueKind == JsonValueKind.String &&
                Loc.Languages.Any(language => language.Code == value.GetString()))
                Saved = value.GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>Persists an offered language; a failed write keeps the previous choice and returns the reason.</summary>
    public string? Save(string language)
    {
        if (Loc.Languages.All(offered => offered.Code != language)) return Loc.T("Choose one of the listed languages.");
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(new { Language = language }));
            Saved = language;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Loc.T("The language could not be saved. Your previous language still applies.");
        }
    }
}
