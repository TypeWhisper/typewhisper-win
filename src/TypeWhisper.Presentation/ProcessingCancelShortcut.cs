namespace TypeWhisper.Presentation;

/// <summary>Native registration boundary, whose current value is authoritative after a failed change.</summary>
public interface IProcessingCancelShortcutBackend
{
    /// <summary>The currently registered, canonical shortcuts.</summary>
    string Value { get; }
    /// <summary>Changes registration, returning an actionable error on failure.</summary>
    string? TryChange(string value);
}

/// <summary>Persists explicit shortcuts after successful registration; missing settings mean unassigned.</summary>
public sealed class ProcessingCancelShortcut
{
    private readonly string _path;
    private readonly IProcessingCancelShortcutBackend _backend;
    private readonly Func<string, string?> _validate;
    private readonly string _displayName;
    /// <summary>Creates an uninitialized controller; this does not register or write anything.</summary>
    public ProcessingCancelShortcut(string path, IProcessingCancelShortcutBackend backend, Func<string, string?> validate, string displayName = "Cancel shortcuts")
    { _path = Path.GetFullPath(path); _backend = backend; _validate = validate; _displayName = displayName; }
    /// <summary>The actually registered shortcuts, not a failed draft.</summary>
    public string Value => _backend.Value;
    /// <summary>Latest registration or persistence failure.</summary>
    public string? Error { get; private set; }
    /// <summary>Loads bounded settings and registers them without rewriting the file.</summary>
    public string? Initialize()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            if (stream.Length > 1024) return Error = $"{_displayName} could not be loaded. Assign them again in Settings.";
            using var reader = new StreamReader(stream);
            var value = reader.ReadToEnd();
            return Error = Validate(value) ?? _backend.TryChange(value);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Error = $"{_displayName} could not be loaded. Assign them again in Settings."; }
    }
    /// <summary>Registers and atomically saves a canonical value, rolling back registration if saving fails.</summary>
    public string? Save(string value)
    {
        if (Validate(value) is { } invalid) return Error = invalid;
        var previous = Value;
        if (_backend.TryChange(value) is { } unavailable) return Error = unavailable;
        var registeredValue = Value;
        if (!SameBindings(registeredValue, value)) return RollBack(previous);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(registeredValue);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true); temporary = null;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return RollBack(previous); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private string? Validate(string value) => value.Length > 256 || value.Any(char.IsControl)
        ? $"{_displayName}: the shortcut value is invalid or too long." : _validate(value);
    private string RollBack(string previous)
    {
        var error = _backend.TryChange(previous);
        return Error = error is null && SameBindings(Value, previous)
            ? $"{_displayName} could not be saved. Previous shortcuts still apply."
            : $"{_displayName} could not be saved and previous registration could not be restored. The displayed active shortcuts are authoritative; reassign them or restart.";
    }
    private static bool SameBindings(string actual, string requested)
    {
        var left = actual.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var right = requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return left.Length == right.Length && left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
    }

    /// <summary>Checks canonical cancel chords against ordinary shortcuts and modifier-only dictation prefixes.</summary>
    public static bool Conflicts(string cancelValue, string otherValue, bool otherAllowsModifierOnly)
    {
        foreach (var cancel in cancelValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var other in otherValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (cancel == other) return true;
            var parts = other.Split('+');
            if (otherAllowsModifierOnly && parts.Length > 0 && parts.All(IsModifier) && parts.All(cancel.Split('+').Contains)) return true;
        }
        return false;
    }
    private static bool IsModifier(string value) => value is "CTRL" or "ALT" or "SHIFT" or "WIN";
    /// <summary>Requests cancellation only for an active final-processing operation; never starts or retries work.</summary>
    public static void Invoke(bool processingCanBeCanceled, bool shortcutEditorOpen, Action requestCancel)
    { if (processingCanBeCanceled && !shortcutEditorOpen) requestCancel(); }
}
