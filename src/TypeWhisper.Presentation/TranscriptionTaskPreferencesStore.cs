using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Persists native transcription or audio-to-English translation for an explicit profile.</summary>
public sealed class TranscriptionTaskPreferencesStore
{
    private readonly string _path;
    /// <summary>The loaded or last successfully saved task; model changes do not silently reset it.</summary>
    public TranscriptionTask Current { get; private set; } = TranscriptionTask.Transcribe;
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads without writing; missing or invalid settings select transcription.</summary>
    public TranscriptionTaskPreferencesStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Task", out var value) ||
                value.ValueKind != JsonValueKind.String || value.GetString() is not ("Transcribe" or "Translate"))
                throw new JsonException("Invalid transcription task.");
            Current = Enum.Parse<TranscriptionTask>(value.GetString()!);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "Transcription task could not be loaded. Transcribe is selected. Save your choice to restore this preference.";
        }
    }

    /// <summary>Atomically saves the task; failed writes preserve the previous selection.</summary>
    public string? Save(TranscriptionTask task)
    {
        if (!Enum.IsDefined(task)) return Error = "Choose a valid transcription task.";
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(new { Task = task.ToString() }));
            Current = task;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "Transcription task could not be saved. Your previous task still applies.";
        }
    }
}
