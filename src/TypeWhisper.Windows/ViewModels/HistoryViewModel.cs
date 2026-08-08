using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides history view model behavior.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly IDictionaryService _dictionary;
    private readonly SpeechFeedbackService _speechFeedback;
    private readonly HistoryWorkflowRetryService _workflowRetry;
    private readonly IWorkflowService _workflows;
    private readonly DictationRecoveryAudioStore _recoveryStore;
    private int _suppressRefreshDepth;
    private bool _hasLoaded;

    private bool SuppressRefresh => Volatile.Read(ref _suppressRefreshDepth) > 0;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string? _selectedAppFilter;
    [ObservableProperty] private bool _isLoading;

    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];
    /// <summary>
    /// Gets the available apps.
    /// </summary>
    public ObservableCollection<string> AvailableApps { get; } = [];
    /// <summary>
    /// Gets the grouped entries.
    /// </summary>
    public ICollectionView GroupedEntries { get; }

    /// <summary>
    /// Gets the number of persisted transcription history records.
    /// </summary>
    public int TotalRecords => _history.TotalRecords;
    /// <summary>
    /// Gets the total words.
    /// </summary>
    public int TotalWords => _history.TotalWords;

    /// <summary>
    /// Initializes a new instance of the HistoryViewModel class.
    /// </summary>
    public HistoryViewModel(
        IHistoryService history,
        IDictionaryService dictionary,
        SpeechFeedbackService speechFeedback,
        HistoryWorkflowRetryService workflowRetry,
        IWorkflowService workflows,
        DictationRecoveryAudioStore recoveryStore)
    {
        _history = history;
        _dictionary = dictionary;
        _speechFeedback = speechFeedback;
        _workflowRetry = workflowRetry;
        _workflows = workflows;
        _recoveryStore = recoveryStore;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        GroupedEntries = CollectionViewSource.GetDefaultView(Entries);
        GroupedEntries.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(HistoryEntryViewModel.DateGroup)));
        GroupedEntries.Filter = FilterEntry;

        _history.RecordsChanged += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (SuppressRefresh) return;
                RefreshRecords();
                OnPropertyChanged(nameof(TotalRecords));
                OnPropertyChanged(nameof(TotalWords));
            });
        };
        _workflows.WorkflowsChanged += () => Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var entry in Entries)
                entry.RefreshWorkflowOptions();
        });
    }

    /// <summary>
    /// Performs load asynchronously.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_hasLoaded) return;

        IsLoading = true;
        try
        {
            await _history.EnsureLoadedAsync().ConfigureAwait(false);
            await Application.Current!.Dispatcher.InvokeAsync(() =>
            {
                _hasLoaded = true;
                RefreshRecords();
                OnPropertyChanged(nameof(TotalRecords));
                OnPropertyChanged(nameof(TotalWords));
            });
        }
        finally
        {
            Application.Current?.Dispatcher.Invoke(() => IsLoading = false);
        }
    }

    partial void OnSearchQueryChanged(string value) => GroupedEntries.Refresh();
    partial void OnSelectedAppFilterChanged(string? value) => GroupedEntries.Refresh();

    private bool FilterEntry(object obj)
    {
        if (obj is not HistoryEntryViewModel entry) return false;

        if (!string.IsNullOrWhiteSpace(SelectedAppFilter) &&
            !string.Equals(entry.Record.AppProcessName, SelectedAppFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;

        var q = SearchQuery;
        return entry.Record.DisplayText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               entry.Record.RawText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (entry.Record.AppName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    private void RefreshRecords()
    {
        Entries.Clear();
        foreach (var r in _history.Records)
            Entries.Add(new HistoryEntryViewModel(r, this));

        RebuildAppFilter();
        GroupedEntries.Refresh();
    }

    private void RebuildAppFilter()
    {
        var current = SelectedAppFilter;
        AvailableApps.Clear();
        foreach (var app in _history.GetDistinctApps())
            AvailableApps.Add(app);
        SelectedAppFilter = current is not null && AvailableApps.Contains(current) ? current : null;
    }

    [RelayCommand]
    private void ClearAll()
    {
        var result = MessageBox.Show(
            Loc.Instance["History.ClearAllConfirm"],
            Loc.Instance["History.ClearAllTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _history.ClearAll();
    }

    [RelayCommand]
    private void ClearAppFilter() => SelectedAppFilter = null;

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = Loc.Instance["History.ExportFileFilter"],
            DefaultExt = ".txt",
            FileName = Loc.Instance.GetString("History.ExportFilename", DateTime.Now)
        };
        if (dlg.ShowDialog() != true) return;

        var visibleRecords = Entries
            .Where(e => GroupedEntries.Filter?.Invoke(e) ?? true)
            .Select(e => e.Record)
            .ToList();

        var labels = new ExportLabels
        {
            Header = Loc.Instance["Export.Header"],
            Exported = Loc.Instance["Export.Exported"],
            Entries = Loc.Instance["Export.Entries"],
            Timestamp = Loc.Instance["Export.Timestamp"],
            App = Loc.Instance["Export.App"],
            Text = Loc.Instance["Export.Text"],
            Duration = Loc.Instance["Export.Duration"],
            Words = Loc.Instance["Export.Words"],
            Language = Loc.Instance["Export.Language"],
        };

        var content = dlg.FilterIndex switch
        {
            2 => _history.ExportToCsv(visibleRecords, labels),
            3 => _history.ExportToMarkdown(visibleRecords, labels),
            4 => _history.ExportToJson(visibleRecords),
            _ => _history.ExportToText(visibleRecords, labels)
        };

        File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8);
    }

    internal void CollapseAllExcept(HistoryEntryViewModel? keep)
    {
        foreach (var entry in Entries)
            if (entry != keep && entry.IsExpanded)
            {
                entry.IsEditing = false;
                entry.IsExpanded = false;
            }
    }

    internal async Task DeleteEntryAsync(HistoryEntryViewModel entry)
    {
        var recoveryFileName = entry.Record.RecoveryAudioFileName;
        _history.DeleteRecord(entry.Record.Id);
        if (_history.Records.Any(record => string.Equals(
                record.Id,
                entry.Record.Id,
                StringComparison.Ordinal)))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(recoveryFileName))
            return;

        var recovery = _recoveryStore.Recordings.FirstOrDefault(candidate =>
            string.Equals(candidate.FileName, recoveryFileName, StringComparison.OrdinalIgnoreCase));
        if (recovery is not null)
            await _recoveryStore.DeleteAsync(recovery.Id);
    }

    internal void ReadBackEntry(HistoryEntryViewModel entry) =>
        _speechFeedback.ReadBack(entry.Record.DisplayText, entry.Record.Language);

    internal IReadOnlyList<Workflow> GetRetryWorkflows() => _workflowRetry.GetEligibleWorkflows();

    internal Workflow? ResolveInitialWorkflow(TranscriptionRecord record) =>
        _workflowRetry.ResolveInitialWorkflow(record);

    internal async Task<TranscriptionRecord> RetryWorkflowAsync(
        HistoryEntryViewModel entry,
        Workflow workflow,
        Func<string, Task> statusCallback,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _suppressRefreshDepth);
        try
        {
            return await _workflowRetry.RetryAsync(
                entry.Record.Id,
                workflow.Id,
                statusCallback,
                cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _suppressRefreshDepth);
            OnPropertyChanged(nameof(TotalRecords));
            OnPropertyChanged(nameof(TotalWords));
        }
    }

    internal TranscriptionRecord? GetCurrentRecord(string id) =>
        _history.Records.FirstOrDefault(record => string.Equals(record.Id, id, StringComparison.Ordinal));

    internal void SaveEdit(HistoryEntryViewModel entry, string newText)
    {
        var originalText = entry.Record.DisplayText;

        // Suppress refresh so RecordsChanged doesn't rebuild entries and lose suggestions
        Interlocked.Increment(ref _suppressRefreshDepth);
        try
        {
            _history.UpdateRecord(entry.Record.Id, newText);
        }
        finally
        {
            Interlocked.Decrement(ref _suppressRefreshDepth);
        }

        // Extract correction suggestions
        var suggestions = TextDiffService.ExtractCorrections(originalText, newText);
        if (suggestions.Count > 0)
        {
            entry.CorrectionSuggestions.Clear();
            foreach (var s in suggestions)
                entry.CorrectionSuggestions.Add(s);
            entry.HasSuggestions = true;
        }

        OnPropertyChanged(nameof(TotalRecords));
        OnPropertyChanged(nameof(TotalWords));
    }

    internal void LearnCorrection(CorrectionSuggestion suggestion) =>
        _dictionary.LearnCorrection(suggestion.Original, suggestion.Replacement);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_hasLoaded)
                RefreshRecords();
        });
    }
}

/// <summary>
/// Provides history entry view model behavior.
/// </summary>
public partial class HistoryEntryViewModel : ObservableObject
{
    private readonly HistoryViewModel _parent;

    /// <summary>
    /// Gets or sets the record value.
    /// </summary>
    public TranscriptionRecord Record { get; private set; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private bool _hasSuggestions;
    [ObservableProperty] private bool _isRetrying;
    [ObservableProperty] private string _retryStatusText = "";
    [ObservableProperty] private string _retryErrorText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryWorkflowCommand))]
    private Workflow? _selectedWorkflow;

    private CancellationTokenSource? _retryCts;

    /// <summary>
    /// Gets the correction suggestions.
    /// </summary>
    public ObservableCollection<CorrectionSuggestion> CorrectionSuggestions { get; } = [];
    /// <summary>
    /// Gets workflows that currently have a post-processing prompt.
    /// </summary>
    public ObservableCollection<Workflow> RetryWorkflows { get; } = [];

    /// <summary>
    /// Gets the timestamp converted from persisted UTC to local time for display.
    /// </summary>
    public DateTime LocalTimestamp => ConvertUtcToLocalTime(Record.Timestamp);
    /// <summary>
    /// Performs compute date group.
    /// </summary>
    public string DateGroup => ComputeDateGroup(LocalTimestamp);
    /// <summary>
    /// Performs time label.
    /// </summary>
    public string TimeLabel => LocalTimestamp.ToString("HH:mm");
    /// <summary>
    /// Gets the duration label.
    /// </summary>
    public string DurationLabel => $"{Record.DurationSeconds:F1}s";
    /// <summary>
    /// Gets whether post-processing failed for this entry.
    /// </summary>
    public bool IsWorkflowFailed => Record.Status == TranscriptionRecordStatus.WorkflowPostProcessingFailed;

    /// <summary>
    /// Initializes a new instance of the HistoryEntryViewModel class.
    /// </summary>
    public HistoryEntryViewModel(TranscriptionRecord record, HistoryViewModel parent)
    {
        Record = record;
        _parent = parent;
        RefreshWorkflowOptions();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _parent.CollapseAllExcept(this);
        else
        {
            IsEditing = false;
            HasSuggestions = false;
            CorrectionSuggestions.Clear();
        }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void StartEdit()
    {
        if (IsWorkflowFailed)
            return;
        EditText = Record.DisplayText;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        _parent.SaveEdit(this, EditText);
        Record = Record with { FinalText = EditText };
        IsEditing = false;
        OnPropertyChanged(nameof(Record));
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void Copy() => Clipboard.SetText(Record.DisplayText);

    [RelayCommand]
    private void CopyRawText() => Clipboard.SetText(Record.RawText);

    [RelayCommand]
    private void ReadBack() => _parent.ReadBackEntry(this);

    [RelayCommand]
    private Task Delete() => _parent.DeleteEntryAsync(this);

    /// <summary>
    /// Refreshes the eligible workflow list while preserving a still-valid selection.
    /// </summary>
    internal void RefreshWorkflowOptions()
    {
        if (!IsWorkflowFailed)
            return;

        var previousId = SelectedWorkflow?.Id;
        RetryWorkflows.Clear();
        foreach (var workflow in _parent.GetRetryWorkflows())
            RetryWorkflows.Add(workflow);

        SelectedWorkflow = RetryWorkflows.FirstOrDefault(workflow => workflow.Id == previousId)
                           ?? _parent.ResolveInitialWorkflow(Record);
    }

    private bool CanRetryWorkflow() => IsWorkflowFailed && SelectedWorkflow is not null && !IsRetrying;

    [RelayCommand(CanExecute = nameof(CanRetryWorkflow))]
    private async Task RetryWorkflow()
    {
        if (SelectedWorkflow is null || IsRetrying)
            return;

        _retryCts?.Dispose();
        _retryCts = new CancellationTokenSource();
        IsRetrying = true;
        RetryErrorText = "";
        RetryStatusText = Loc.Instance["History.RetryStarting"];
        RetryWorkflowCommand.NotifyCanExecuteChanged();
        try
        {
            var updated = await _parent.RetryWorkflowAsync(
                this,
                SelectedWorkflow,
                status => Application.Current?.Dispatcher.InvokeAsync(() => RetryStatusText = status).Task
                          ?? Task.CompletedTask,
                _retryCts.Token);
            Record = updated;
            RetryStatusText = Loc.Instance["History.RetrySucceeded"];
            OnPropertyChanged(nameof(Record));
            OnPropertyChanged(nameof(IsWorkflowFailed));
        }
        catch (OperationCanceledException)
        {
            RetryStatusText = Loc.Instance["Status.Cancelled"];
        }
        catch (Exception ex)
        {
            Record = _parent.GetCurrentRecord(Record.Id) ?? Record;
            RetryErrorText = HistoryWorkflowRetryService.SanitizeFailure(ex);
            RetryStatusText = "";
            OnPropertyChanged(nameof(Record));
            OnPropertyChanged(nameof(IsWorkflowFailed));
        }
        finally
        {
            IsRetrying = false;
            _retryCts.Dispose();
            _retryCts = null;
            RetryWorkflowCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void AcceptSuggestions()
    {
        foreach (var s in CorrectionSuggestions)
            _parent.LearnCorrection(s);
        HasSuggestions = false;
        CorrectionSuggestions.Clear();
    }

    [RelayCommand]
    private void DismissSuggestions()
    {
        HasSuggestions = false;
        CorrectionSuggestions.Clear();
    }

    internal static DateTime ConvertUtcToLocalTime(DateTime timestamp, TimeZoneInfo? timeZone = null)
    {
        var utcTimestamp = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        return TimeZoneInfo.ConvertTimeFromUtc(utcTimestamp, timeZone ?? TimeZoneInfo.Local);
    }

    private static string ComputeDateGroup(DateTime timestamp)
    {
        var today = DateTime.Today;
        var date = timestamp.Date;

        if (date == today) return Loc.Instance["History.Today"];
        if (date == today.AddDays(-1)) return Loc.Instance["History.Yesterday"];

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-daysSinceMonday);
        if (date >= thisMonday) return Loc.Instance["History.ThisWeek"];

        var lastMonday = thisMonday.AddDays(-7);
        if (date >= lastMonday) return Loc.Instance["History.LastWeek"];

        return timestamp.ToString("MMMM yyyy");
    }
}
