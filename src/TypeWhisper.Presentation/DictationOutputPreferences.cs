using System.Text.Json;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Controls the persistent and automatic destinations of a dictation result.</summary>
public sealed record DictationOutputPreferences
{
    /// <summary>Whether to attempt insertion without first opening a review.</summary>
    public bool AutoPaste { get; init; } = true;
    /// <summary>Restore and verify the editor captured at recording start before inserting.</summary>
    public bool LockPasteToFocusedField { get; init; }
    /// <summary>Without automatic insertion, copy the result to the clipboard instead of opening a review.</summary>
    public bool CopyInsteadOfReview { get; init; }
    /// <summary>Restore the previous clipboard after pasting; off leaves the inserted text on the clipboard.</summary>
    public bool PreserveClipboard { get; init; } = true;
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
        CopyInsteadOfReview = CopyInsteadOfReview && current.CopyInsteadOfReview,
        // Keeping the previous clipboard is the protective choice, so turning it on applies to work in progress.
        PreserveClipboard = PreserveClipboard || current.PreserveClipboard,
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
                SaveHistoryAudio = root.TryGetProperty(nameof(DictationOutputPreferences.SaveHistoryAudio), out var audio) && audio.GetBoolean(),
                CopyInsteadOfReview = root.TryGetProperty(nameof(DictationOutputPreferences.CopyInsteadOfReview), out var copy) && copy.GetBoolean(),
                PreserveClipboard = !root.TryGetProperty(nameof(DictationOutputPreferences.PreserveClipboard), out var preserve) || preserve.GetBoolean()
            };
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Current = new() { AutoPaste = false, SaveToHistory = false };
            Error = Loc.T("Output preferences could not be loaded. Automatic paste and history saving are off. Save your choices to restore them.");
        }
    }

    /// <summary>Atomically persists choices; a failed write preserves the current choices.</summary>
    public string? Save(DictationOutputPreferences next)
    {
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(next));
            Current = next;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = Loc.T("Output preferences could not be saved. Your previous choices still apply.");
        }
    }
}
