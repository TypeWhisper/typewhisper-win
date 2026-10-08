using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

/// <summary>A connected input device as reported by GET /v1/settings/audio.</summary>
internal sealed record LocalApiAudioInput(string Id, string Name, bool IsSystemDefault);

/// <summary>The live audio settings the API reads and changes; the app passes its dictation session.</summary>
internal interface ILocalApiAudioTarget
{
    /// <summary>False while recording, processing or another session operation is running.</summary>
    bool CanChange { get; }
    IReadOnlyList<LocalApiAudioInput> InputDevices { get; }
    IReadOnlyList<MicrophonePriorityItem> InputPriority { get; }
    /// <summary>The microphone the next recording would open, or null when no listed or default device is available.</summary>
    MicrophonePriorityItem? ActiveInput { get; }
    DictationAudioPreferences Preferences { get; }
    /// <summary>Applies and saves the priority like the settings editor; returns an error message on failure.</summary>
    string? SetInputPriority(IReadOnlyList<MicrophonePriorityItem> priority);
    /// <summary>Applies and saves audio.json like the settings page; returns an error message on failure.</summary>
    string? SavePreferences(DictationAudioPreferences preferences);
}

// GET and PATCH /v1/settings/audio. Calls run synchronously on the app dispatcher, like settings edits.
// PATCH validates the whole body before changing anything, so a rejected request leaves both files as they were.
internal sealed class LocalApiAudioSettings(ILocalApiAudioTarget target)
{
    internal const string Path = "/v1/settings/audio";
    private const int MaxPriorityEntries = 100;
    private const int MaxDeviceTextLength = 1024;
    private static readonly string[] ReadOnlyFields = ["input_devices", "active_input"];

    internal LocalApiResponse? Handle(LocalApiRequest request, CancellationToken ct = default)
    {
        if (request.Path != Path) return null;
        ct.ThrowIfCancellationRequested();
        if (request.Method is not ("GET" or "PATCH")) return Error(405, "Use GET or PATCH.");
        if (request.Query.Count != 0) return Error(400, "This endpoint accepts no query parameters.");
        if (request.Method == "GET")
            return request.Body.Length != 0 ? Error(400, "GET accepts no request body.") : State();
        Patch patch;
        try { patch = Parse(request); }
        catch (AudioSettingsException ex) { return Error(400, ex.Message); }
        catch (JsonException) { return Error(400, "Invalid JSON request."); }
        ct.ThrowIfCancellationRequested();
        if (!target.CanChange) return Error(409, "Finish active recording and processing before changing audio settings.");

        var previousPriority = target.InputPriority.ToArray();
        var preferences = target.Preferences;
        var next = preferences with
        {
            AudioDuckingEnabled = patch.AudioDuckingEnabled ?? preferences.AudioDuckingEnabled,
            AudioDuckingLevel = patch.AudioDuckingLevel ?? preferences.AudioDuckingLevel,
            PauseMediaDuringRecording = patch.PauseMediaDuringRecording ?? preferences.PauseMediaDuringRecording,
            SoundFeedbackEnabled = patch.SoundFeedbackEnabled ?? preferences.SoundFeedbackEnabled
        };
        var priorityChanged = patch.InputPriority is { } priority && !priority.SequenceEqual(previousPriority);
        // Keep the request all-or-nothing: a failed step puts the previous microphone order back.
        if (priorityChanged && target.SetInputPriority(patch.InputPriority!) is { } priorityError)
            return Failure(priorityError, RestorePriority(previousPriority));
        if (next != preferences && target.SavePreferences(next) is { } preferencesError)
            return Failure(preferencesError, priorityChanged ? RestorePriority(previousPriority) : null);
        return State();
    }

    // A setter can fail after it has already saved and applied the new order.
    private string? RestorePriority(IReadOnlyList<MicrophonePriorityItem> previous) =>
        target.InputPriority.SequenceEqual(previous) ? null : target.SetInputPriority(previous);

    private LocalApiResponse Failure(string message, string? rollbackError) =>
        rollbackError is not null
            ? Error(500, $"Audio settings could not be saved: {message} The previous microphone priority could not be restored either: {rollbackError}")
            : target.CanChange
                ? Error(500, "Audio settings could not be saved: " + message)
                : Error(409, "Finish active recording and processing before changing audio settings.");

    private LocalApiResponse State()
    {
        var preferences = target.Preferences;
        return LocalApiResponse.Json(200, new
        {
            input_devices = target.InputDevices.Select(device => new { id = device.Id, name = device.Name, is_system_default = device.IsSystemDefault }),
            input_priority = target.InputPriority.Select(item => new { id = item.Id, name = item.Name }),
            active_input = target.ActiveInput is { } active ? new { id = active.Id, name = active.Name } : null,
            audio_ducking_enabled = preferences.AudioDuckingEnabled,
            // Kept as float so a GET value PATCHed back is stored bit-identically.
            audio_ducking_level = preferences.AudioDuckingLevel,
            pause_media_during_recording = preferences.PauseMediaDuringRecording,
            sound_feedback_enabled = preferences.SoundFeedbackEnabled
        });
    }

    private sealed record Patch(IReadOnlyList<MicrophonePriorityItem>? InputPriority, bool? AudioDuckingEnabled,
        float? AudioDuckingLevel, bool? PauseMediaDuringRecording, bool? SoundFeedbackEnabled);

    private static Patch Parse(LocalApiRequest request)
    {
        if (request.Body.Length == 0) throw new AudioSettingsException("Provide a JSON object with the audio settings to change.");
        if (request.ContentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
            throw new AudioSettingsException("Use Content-Type: application/json.");
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AudioSettingsException("Expected a JSON object.");
        IReadOnlyList<MicrophonePriorityItem>? priority = null;
        bool? ducking = null, pauseMedia = null, sounds = null;
        float? level = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw new AudioSettingsException($"Duplicate field '{field.Name}'.");
            switch (field.Name)
            {
                case "input_priority": priority = Priority(field.Value); break;
                case "audio_ducking_enabled": ducking = Boolean(field); break;
                case "pause_media_during_recording": pauseMedia = Boolean(field); break;
                case "sound_feedback_enabled": sounds = Boolean(field); break;
                case "audio_ducking_level":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var value)
                        || !double.IsFinite(value) || value is < 0 or > 1)
                        throw new AudioSettingsException("'audio_ducking_level' must be a number from 0 to 1.");
                    level = (float)value;
                    break;
                default:
                    throw new AudioSettingsException(ReadOnlyFields.Contains(field.Name)
                        ? $"'{field.Name}' is read-only."
                        : $"Unknown or unsupported field '{field.Name}'.");
            }
        }
        return new(priority, ducking, level, pauseMedia, sounds);
    }

    private static bool Boolean(JsonProperty field) => field.Value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new AudioSettingsException($"'{field.Name}' must be true or false.")
    };

    // Entries are stored exactly as sent, so a list read with GET restores without changes.
    // Disconnected microphones are allowed; the first available entry wins at recording time.
    private static IReadOnlyList<MicrophonePriorityItem> Priority(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxPriorityEntries)
            throw new AudioSettingsException($"'input_priority' must be an array of at most {MaxPriorityEntries} devices.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<MicrophonePriorityItem>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new AudioSettingsException("Each 'input_priority' entry must be an object with 'id' and 'name'.");
            string? id = null, name = null;
            foreach (var field in item.EnumerateObject())
            {
                if (field.Name is not ("id" or "name") || (field.Name == "id" ? id : name) is not null)
                    throw new AudioSettingsException("Each 'input_priority' entry accepts only one 'id' and one 'name'.");
                if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString()!.Length > MaxDeviceTextLength)
                    throw new AudioSettingsException($"'input_priority' {field.Name} must be a string of at most {MaxDeviceTextLength} characters.");
                if (field.Name == "id") id = field.Value.GetString(); else name = field.Value.GetString();
            }
            if (string.IsNullOrWhiteSpace(id) || name is null)
                throw new AudioSettingsException("Each 'input_priority' entry needs a non-empty 'id' and a 'name'.");
            if (!ids.Add(id)) throw new AudioSettingsException($"'input_priority' lists '{id}' more than once.");
            items.Add(new MicrophonePriorityItem(id, name));
        }
        return items;
    }

    private static LocalApiResponse Error(int status, string message) => LocalApiResponse.Json(status, new
    {
        error = new
        {
            code = status switch { 400 => "bad_request", 405 => "method_not_allowed", 409 => "conflict", _ => "error" },
            message = Loc.English(message)
        }
    });

    private sealed class AudioSettingsException(string message) : Exception(message);
}
