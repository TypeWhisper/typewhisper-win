using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>An explicit local-history retention choice. New profiles keep history indefinitely.</summary>
public sealed record HistoryRetentionPreferences(
    HistoryRetentionMode HistoryRetentionMode = HistoryRetentionMode.Forever,
    int HistoryRetentionMinutes = 90 * 24 * 60)
{
    /// <summary>The longest supported retention duration in minutes.</summary>
    public const int MaximumMinutes = 10 * 365 * 24 * 60;

    /// <summary>Only indefinite retention and a positive, bounded duration are supported.</summary>
    public bool IsValid => HistoryRetentionMode is HistoryRetentionMode.Forever or HistoryRetentionMode.Duration
        && HistoryRetentionMinutes is >= 1 and <= MaximumMinutes;

    /// <summary>Matches the existing history service's strict creation-time cutoff.</summary>
    public bool IsExpired(DateTime createdAt, DateTime utcNow) => IsValid
        && HistoryRetentionMode == HistoryRetentionMode.Duration
        && createdAt < utcNow.AddMinutes(-HistoryRetentionMinutes);

    /// <summary>Shortening retention can delete existing history and requires explicit confirmation.</summary>
    public bool RequiresConfirmationComparedTo(HistoryRetentionPreferences previous) => IsValid
        && HistoryRetentionMode == HistoryRetentionMode.Duration
        && (!previous.IsValid || previous.HistoryRetentionMode != HistoryRetentionMode.Duration
            || HistoryRetentionMinutes < previous.HistoryRetentionMinutes);
}

/// <summary>Loads and atomically saves retention choices in an explicitly supplied profile.</summary>
public sealed class HistoryRetentionPreferencesStore
{
    private readonly string _path;
    /// <summary>The last successfully loaded or saved choice.</summary>
    public HistoryRetentionPreferences Current { get; private set; } = new();
    /// <summary>False after a malformed or unreadable settings file, until a choice is saved.</summary>
    public bool CanApply { get; private set; } = true;
    /// <summary>A visible settings error. Failed saves leave the previous choice active.</summary>
    public string? Error { get; private set; }

    /// <summary>Missing settings default to forever without creating a file.</summary>
    public HistoryRetentionPreferencesStore(string path)
    {
        _path = path;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("HistoryRetentionMode", out var modeValue)
                || modeValue.ValueKind != JsonValueKind.String
                || !Enum.TryParse<HistoryRetentionMode>(modeValue.GetString(), out var mode)
                || !root.TryGetProperty("HistoryRetentionMinutes", out var minutesValue)
                || !minutesValue.TryGetInt32(out var minutes))
                throw new JsonException("Invalid history retention settings.");
            var preferences = new HistoryRetentionPreferences(mode, minutes);
            if (!preferences.IsValid) throw new JsonException("Invalid history retention settings.");
            Current = preferences;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            CanApply = false;
            Error = "History retention could not be loaded. Automatic deletion is paused. Apply a retention choice to restore it.";
        }
    }

    /// <summary>Persists a choice before making it active; failures preserve the previous policy.</summary>
    public string? Save(HistoryRetentionPreferences preferences)
    {
        if (!preferences.IsValid) return Error = "Choose Forever or a duration between 1 minute and 10 years. Your previous choice still applies.";
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".history-retention-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                HistoryRetentionMode = preferences.HistoryRetentionMode.ToString(), preferences.HistoryRetentionMinutes
            }));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = preferences;
            CanApply = true;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var active = !CanApply ? "Automatic deletion remains paused."
                : Current.HistoryRetentionMode == HistoryRetentionMode.Forever ? "Forever remains active."
                : $"Automatic deletion after {Current.HistoryRetentionMinutes:N0} minutes remains active.";
            return Error = "History retention could not be saved. " + active;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
