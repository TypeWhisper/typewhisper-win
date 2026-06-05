using System.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides history retention coordinator behavior.
/// </summary>
public sealed class HistoryRetentionCoordinator : IDisposable
{
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private int _applyInProgress;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the HistoryRetentionCoordinator class.
    /// </summary>
    public HistoryRetentionCoordinator(IHistoryService history, ISettingsService settings)
    {
        _history = history;
        _settings = settings;
    }

    /// <summary>
    /// Initializes resources required before use.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        _settings.SettingsChanged += OnSettingsChanged;
        _history.RecordsChanged += OnRecordsChanged;
        _initialized = true;

        ApplyRetention(_settings.Current, HistoryRetentionTrigger.Startup);
    }

    /// <summary>
    /// Performs handle shutdown.
    /// </summary>
    public void HandleShutdown()
    {
        if (!_initialized) return;
        ApplyRetention(_settings.Current, HistoryRetentionTrigger.Shutdown);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (!_initialized) return;
        ApplyRetention(settings, HistoryRetentionTrigger.SettingsChanged);
    }

    private void OnRecordsChanged()
    {
        if (!_initialized || Volatile.Read(ref _applyInProgress) != 0) return;
        ApplyRetention(_settings.Current, HistoryRetentionTrigger.HistoryChanged);
    }

    private void ApplyRetention(AppSettings settings, HistoryRetentionTrigger trigger)
    {
        if (Interlocked.Exchange(ref _applyInProgress, 1) != 0)
            return;

        try
        {
            switch (settings.HistoryRetentionMode)
            {
                case HistoryRetentionMode.Duration:
                    _history.PurgeOldRecords(TimeSpan.FromMinutes(settings.HistoryRetentionMinutes));
                    break;
                case HistoryRetentionMode.UntilAppCloses when trigger is HistoryRetentionTrigger.Startup or HistoryRetentionTrigger.Shutdown:
                    _history.ClearAll();
                    break;
            }
        }
        finally
        {
            Volatile.Write(ref _applyInProgress, 0);
        }
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (!_initialized) return;

        _settings.SettingsChanged -= OnSettingsChanged;
        _history.RecordsChanged -= OnRecordsChanged;
        _initialized = false;
    }

    private enum HistoryRetentionTrigger
    {
        Startup,
        SettingsChanged,
        HistoryChanged,
        Shutdown
    }
}
