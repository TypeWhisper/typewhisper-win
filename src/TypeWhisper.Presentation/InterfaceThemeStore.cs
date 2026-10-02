using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>The appearance of the app's windows.</summary>
public enum InterfaceTheme
{
    /// <summary>Follows the Windows app mode.</summary>
    System,
    /// <summary>Light windows, whatever Windows uses.</summary>
    Light,
    /// <summary>Dark windows, whatever Windows uses.</summary>
    Dark
}

/// <summary>Loads and atomically saves the chosen interface theme in an explicitly supplied profile.</summary>
public sealed class InterfaceThemeStore
{
    private readonly string _path;
    /// <summary>The saved theme; <see cref="InterfaceTheme.System"/> while nothing was chosen.</summary>
    public InterfaceTheme Saved { get; private set; }

    /// <summary>Loads the choice without writing; a missing or invalid file follows Windows.</summary>
    public InterfaceThemeStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Theme", out var value) && value.ValueKind == JsonValueKind.String &&
                Enum.GetNames<InterfaceTheme>().Contains(value.GetString()))
                Saved = Enum.Parse<InterfaceTheme>(value.GetString()!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or unreadable file follows Windows.
        }
    }

    /// <summary>Persists the theme; a failed write keeps the previous choice and returns the reason.</summary>
    public string? Save(InterfaceTheme theme)
    {
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(new { Theme = theme.ToString() }));
            Saved = theme;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Loc.T("The theme could not be saved. Your previous theme still applies.");
        }
    }
}
