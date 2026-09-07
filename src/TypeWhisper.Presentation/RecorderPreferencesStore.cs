using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Source defaults for the next recorder capture. Output remains mixed mono WAV.</summary>
public sealed record RecorderPreferences
{
    /// <summary>Whether the microphone is selected for a new recording.</summary>
    public bool MicrophoneEnabled { get; init; } = true;
    /// <summary>Whether system audio is selected for a new recording.</summary>
    public bool SystemAudioEnabled { get; init; }
    /// <summary>The loopback endpoint ID, or null to use the Windows default output.</summary>
    public string? OutputDeviceId { get; init; }
    /// <summary>Whether the endpoint ID can be persisted without malformed control characters.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => OutputDeviceId is null || OutputDeviceId.Length <= 2048 && !OutputDeviceId.Any(char.IsControl);
}

/// <summary>Atomically stores recorder preferences in an explicit profile. Mutations belong to the UI owner thread.</summary>
public sealed class RecorderPreferencesStore
{
    private readonly string _path;
    /// <summary>The last successfully loaded or saved immutable selection.</summary>
    public RecorderPreferences Current { get; private set; } = new();
    /// <summary>A load or save error, without overwriting the previous file.</summary>
    public string? Error { get; private set; }
    /// <summary>Raised on the owner thread after a save attempt.</summary>
    public event Action? Changed;

    /// <summary>Loads existing preferences; absent settings use microphone on, system audio off and the default output.</summary>
    public RecorderPreferencesStore(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            var value = JsonSerializer.Deserialize<RecorderPreferences>(File.ReadAllText(_path));
            if (value is null || !value.IsValid) throw new JsonException("Invalid recorder preferences.");
            Current = Normalize(value);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = "Recorder preferences could not be loaded. Default sources are selected; the existing file was preserved."; }
    }

    /// <summary>Saves source defaults atomically; a failed save retains the previous selection and file.</summary>
    public string? Save(RecorderPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsValid) { Error = "Choose a valid system audio device."; NotifyChanged(); return Error; }
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            var normalized = Normalize(value);
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = normalized;
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "Recorder preferences could not be saved. The previous source selection still applies."; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        NotifyChanged();
        return Error;
    }

    private static RecorderPreferences Normalize(RecorderPreferences value) =>
        value with { OutputDeviceId = string.IsNullOrWhiteSpace(value.OutputDeviceId) ? null : value.OutputDeviceId };

    private void NotifyChanged()
    {
        if (Changed is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { System.Diagnostics.Trace.TraceError("Recorder preference observer failed: {0}", ex); }
    }
}
