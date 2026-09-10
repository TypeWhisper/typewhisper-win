using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Determines how pressing and releasing the dictation shortcut controls capture.</summary>
public enum RecordingMode
{
    /// <summary>A tap toggles capture; holding the shortcut stops capture on release.</summary>
    Hybrid,
    /// <summary>Each press starts or stops capture, independent of press duration.</summary>
    Toggle,
    /// <summary>Capture lasts only while the shortcut remains held.</summary>
    Hold
}

/// <summary>Loads and atomically saves the recording mode in an explicitly supplied profile.</summary>
public sealed class RecordingModePreferencesStore
{
    private readonly string _path;
    /// <summary>The loaded or last successfully saved recording mode.</summary>
    public RecordingMode Current { get; private set; } = RecordingMode.Hybrid;
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads preferences without writing; missing or invalid settings use Hybrid.</summary>
    public RecordingModePreferencesStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Mode", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<RecordingMode>(value.GetString(), out var mode) || !Enum.IsDefined(mode))
                throw new JsonException("Invalid recording mode.");
            Current = mode;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "Recording mode could not be loaded. Hybrid is active. Save a mode to restore this preference.";
        }
    }

    /// <summary>Persists a valid mode; failed writes preserve the previous selection.</summary>
    public string? Save(RecordingMode mode)
    {
        if (!Enum.IsDefined(mode)) return Error = "Choose a valid recording mode.";
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Mode = mode.ToString() }));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = mode;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "Recording mode could not be saved. Your previous mode still applies.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
