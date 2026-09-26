using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Determines how the Escape key cancels an active dictation, matching the macOS app.</summary>
public enum EscapeCancelBehavior
{
    /// <summary>The first press shows a warning; a second press within the confirmation window cancels, then a banner confirms.</summary>
    Double,
    /// <summary>A single press cancels, then a banner confirms.</summary>
    Single,
    /// <summary>A single press cancels without a banner.</summary>
    Instant
}

/// <summary>The dictation phase an Escape press would cancel.</summary>
public enum EscapeCancelTarget
{
    /// <summary>Capture is starting or running; cancelling discards the audio.</summary>
    Recording,
    /// <summary>Transcription or post-processing is running; cancelling discards the result.</summary>
    Processing
}

/// <summary>The result of an Escape press while cancellation is available.</summary>
public enum EscapeCancelDecision
{
    /// <summary>Show a warning and wait for the confirming press.</summary>
    Warn,
    /// <summary>Cancel the dictation now.</summary>
    Cancel
}

/// <summary>Tracks the double-press confirmation. Timestamps are monotonic milliseconds.</summary>
public sealed class EscapeCancelConfirmation
{
    /// <summary>How long a warning waits for the confirming press.</summary>
    public const long WindowMilliseconds = 3000;
    /// <summary>How long the cancellation banner stays visible for Double and Single.</summary>
    public const long BannerMilliseconds = 1500;
    private long _deadline;

    /// <summary>The phase the pending warning applies to, if any.</summary>
    public EscapeCancelTarget? Armed { get; private set; }

    /// <summary>Handles one physical press for the phase that is currently cancellable.</summary>
    public EscapeCancelDecision Press(EscapeCancelTarget target, EscapeCancelBehavior behavior, long now)
    {
        if (behavior != EscapeCancelBehavior.Double || Armed == target && now < _deadline)
        {
            Clear();
            return EscapeCancelDecision.Cancel;
        }
        Armed = target;
        _deadline = now + WindowMilliseconds;
        return EscapeCancelDecision.Warn;
    }

    /// <summary>Drops a warning once it expired or its phase ended; returns whether it was dropped.</summary>
    public bool Observe(EscapeCancelTarget? current, long now)
    {
        if (Armed is null || Armed == current && now < _deadline) return false;
        Clear();
        return true;
    }

    /// <summary>Drops any pending warning.</summary>
    public void Clear() => Armed = null;
}

/// <summary>
/// Decides which physical Escape events a low-level hook owns. Once a press is handled, its
/// repeats and release are consumed too, so the focused app never sees half a keystroke.
/// </summary>
public sealed class EscapeKeyFilter
{
    // Auto-repeat key-downs are stamped well within this gap. A longer gap between two key-downs
    // means the release was lost, e.g. while Windows skipped a slow hook, so the second is a new press.
    // A release is never reclassified: without auto-repeat a held key sends no events until it.
    internal const uint LostReleaseMilliseconds = 1500;
    private bool _down;
    private bool _owned;
    private uint _lastDown;

    /// <summary>
    /// Returns whether to consume the event and whether it is a new handled press.
    /// <paramref name="time"/> is the event's own tick-count timestamp, not the time the hook runs.
    /// </summary>
    public (bool Consume, bool Pressed) Key(bool down, uint time, bool available, bool modifiersHeld)
    {
        if (down)
        {
            // Unsigned subtraction stays correct across the 49.7-day tick-count wrap.
            if (_down && unchecked(time - _lastDown) > LostReleaseMilliseconds) Reset();
            _lastDown = time;
        }
        if (!down)
        {
            var owned = _owned;
            Reset();
            return (owned, false);
        }
        var repeat = _down;
        _down = true;
        if (_owned) return (true, false);
        // Ctrl+Esc, Alt+Esc and Ctrl+Shift+Esc remain Windows shortcuts.
        if (repeat || !available || modifiersHeld) return (false, false);
        _owned = true;
        return (true, true);
    }

    /// <summary>Forgets the key state, e.g. after the hook was reinstalled.</summary>
    public void Reset() { _down = false; _owned = false; }
}

/// <summary>Loads and atomically saves the Escape cancellation behavior in an explicitly supplied profile.</summary>
public sealed class EscapeCancelPreferencesStore
{
    private readonly string _path;
    /// <summary>The loaded or last successfully saved behavior.</summary>
    public EscapeCancelBehavior Current { get; private set; } = EscapeCancelBehavior.Double;
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads preferences without writing; missing or invalid settings require a double press.</summary>
    public EscapeCancelPreferencesStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Behavior", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<EscapeCancelBehavior>(value.GetString(), out var behavior) || !Enum.IsDefined(behavior) ||
                int.TryParse(value.GetString(), out _))
                throw new JsonException("Invalid Escape cancellation behavior.");
            Current = behavior;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "Escape cancellation could not be loaded. Pressing Esc twice cancels. Save a choice to restore this preference.";
        }
    }

    /// <summary>Persists a valid behavior; failed writes preserve the previous selection.</summary>
    public string? Save(EscapeCancelBehavior behavior)
    {
        if (!Enum.IsDefined(behavior)) return Error = "Choose a valid Escape behavior.";
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Behavior = behavior.ToString() }));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = behavior;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "Escape cancellation could not be saved. Your previous choice still applies.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
