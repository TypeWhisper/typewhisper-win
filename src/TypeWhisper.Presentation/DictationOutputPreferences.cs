using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Controls the persistent and automatic destinations of a dictation result.</summary>
public sealed record DictationOutputPreferences
{
    /// <summary>Whether to attempt insertion without first opening a review.</summary>
    public bool AutoPaste { get; init; } = true;
    /// <summary>Restore and verify the editor captured at recording start before inserting.</summary>
    public bool LockPasteToFocusedField { get; init; }
    /// <summary>Whether new results may be added to local history.</summary>
    public bool SaveToHistory { get; init; } = true;

    /// <summary>Whether eligible dictations may retain original audio in History; disabled by default.</summary>
    public bool SaveHistoryAudio { get; init; }

    // Turning an option off also applies to work already in progress. Turning it
    // on never gives an older recording additional permission to write or paste.
    /// <summary>Combines recording-start choices with any restrictions applied before delivery.</summary>
    public DictationOutputPreferences RestrictedBy(DictationOutputPreferences current) => new()
    {
        AutoPaste = AutoPaste && current.AutoPaste,
        LockPasteToFocusedField = LockPasteToFocusedField || current.LockPasteToFocusedField,
        SaveToHistory = SaveToHistory && current.SaveToHistory,
        SaveHistoryAudio = SaveToHistory && current.SaveToHistory && SaveHistoryAudio && current.SaveHistoryAudio
    };
}

/// <summary>Loads and atomically saves output choices in an explicitly supplied profile.</summary>
public sealed class DictationOutputPreferencesStore
{
    private readonly string _path;
    /// <summary>The loaded or last successfully saved choices.</summary>
    public DictationOutputPreferences Current
    {
        get => Volatile.Read(ref _current);
        private set => Volatile.Write(ref _current, value);
    }
    private DictationOutputPreferences _current = new();
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads preferences without writing; unreadable files disable both output destinations.</summary>
    public DictationOutputPreferencesStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            // Incomplete/corrupt preferences must not silently enable writes.
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(nameof(DictationOutputPreferences.AutoPaste), out var paste) ||
                !root.TryGetProperty(nameof(DictationOutputPreferences.SaveToHistory), out var history))
                throw new JsonException("Incomplete output preferences.");
            if (root.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new JsonException("Duplicate output preference.");
            Current = new()
            {
                AutoPaste = paste.GetBoolean(), SaveToHistory = history.GetBoolean(),
                LockPasteToFocusedField = root.TryGetProperty(nameof(DictationOutputPreferences.LockPasteToFocusedField), out var locked) && locked.GetBoolean(),
                SaveHistoryAudio = root.TryGetProperty(nameof(DictationOutputPreferences.SaveHistoryAudio), out var audio) && audio.GetBoolean()
            };
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Current = new() { AutoPaste = false, SaveToHistory = false };
            Error = "Output preferences could not be loaded. Automatic paste and history saving are off. Save your choices to restore them.";
        }
    }

    /// <summary>Atomically persists choices; a failed write preserves the current choices.</summary>
    public string? Save(DictationOutputPreferences next)
    {
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = next;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "Output preferences could not be saved. Your previous choices still apply.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
