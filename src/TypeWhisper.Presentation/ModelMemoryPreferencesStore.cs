using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Loads and atomically saves when idle local models are released, in an explicitly supplied profile.</summary>
public sealed class ModelMemoryPreferencesStore
{
    /// <summary>Ten minutes, like macOS.</summary>
    public const int DefaultSeconds = 600;
    /// <summary>Keeps models loaded until they are unloaded explicitly.</summary>
    public const int Never = 0;
    /// <summary>Releases models right after each use.</summary>
    public const int Immediately = -1;
    /// <summary>The offered choices, matching the macOS picker.</summary>
    public static IReadOnlyList<int> Choices { get; } = [Never, Immediately, 120, 300, 600, 1800, 3600];
    private readonly string _path;
    /// <summary>The loaded or last successfully saved idle duration in seconds.</summary>
    public int AutoUnloadSeconds { get; private set; } = DefaultSeconds;
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads preferences without writing; missing or invalid settings use ten minutes.</summary>
    public ModelMemoryPreferencesStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("AutoUnloadSeconds", out var value) ||
                !value.TryGetInt32(out var seconds) || !Choices.Contains(seconds))
                throw new JsonException("Invalid model memory preference.");
            AutoUnloadSeconds = seconds;
        }
        catch (FileNotFoundException) { /* Nothing saved yet: the default applies. */ }
        catch (DirectoryNotFoundException) { /* Nothing saved yet: the default applies. */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Error = "The model unload setting could not be loaded. Models are released after 10 minutes. Choose a setting to restore it.";
        }
    }

    /// <summary>Persists one of <see cref="Choices"/>; failed writes preserve the previous setting.</summary>
    public string? Save(int seconds)
    {
        if (!Choices.Contains(seconds)) return Error = "Choose a valid unload setting.";
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Join(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { AutoUnloadSeconds = seconds }));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            AutoUnloadSeconds = seconds;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "The model unload setting could not be saved. The previous setting still applies.";
        }
        finally
        {
            if (temporary is not null)
                // A leftover dot-named temporary file is harmless and never read as the setting.
                try { File.Delete(temporary); }
                catch (IOException) { /* Left in place, see above. */ }
                catch (UnauthorizedAccessException) { /* Left in place, see above. */ }
        }
    }
}
