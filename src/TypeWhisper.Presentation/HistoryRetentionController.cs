using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Applies explicit retention settings. The host owns periodic scheduling and UI dispatch.</summary>
public sealed class HistoryRetentionController(IHistoryService history, HistoryRetentionPreferencesStore preferences)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _applyError;
    /// <summary>The profile's persisted preferences.</summary>
    public HistoryRetentionPreferencesStore Preferences { get; } = preferences;
    /// <summary>Raised after applying or saving retention. UI consumers must dispatch to their UI thread.</summary>
    public event Action? Changed;
    /// <summary>The last settings or purge failure, suitable for a visible status message.</summary>
    public string? Error => string.Join(" ", new[] { Preferences.Error, _applyError }.Where(value => value is not null)) is { Length: > 0 } error ? error : null;

    /// <summary>Persists a selection and applies it immediately; shortening requires prior UI confirmation.</summary>
    public async Task<string?> ChangeAsync(HistoryRetentionPreferences value, bool confirmedShortening = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (value.RequiresConfirmationComparedTo(Preferences.Current) && !confirmedShortening)
                return "Confirm deletion of existing entries older than the selected duration before applying this choice.";
            if (Preferences.Save(value) is { } error) return error;
            return await ApplyCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    /// <summary>Runs at startup and periodically. Invalid settings and Forever never purge history.</summary>
    public async Task<string?> ApplyAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await ApplyCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    private async Task<string?> ApplyCoreAsync()
    {
        _applyError = null;
        var value = Preferences.Current;
        if (!Preferences.CanApply || !value.IsValid || value.HistoryRetentionMode == HistoryRetentionMode.Forever)
            return Error;
        try
        {
            await history.EnsureLoadedAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var expiredIds = history.Records.Where(record => value.IsExpired(record.CreatedAt, now))
                .Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            history.PurgeOldRecords(TimeSpan.FromMinutes(value.HistoryRetentionMinutes));
            if (history.Records.Any(record => expiredIds.Contains(record.Id)))
                _applyError = "The retention choice is saved, but older history entries could not be deleted. The app will retry.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _applyError = "History retention could not be applied. The app will retry; older entries may still be present.";
        }
        return Error;
    }
}
