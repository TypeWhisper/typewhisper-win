using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides file transcription view model behavior.
/// </summary>
public partial class FileTranscriptionViewModel : ObservableObject
{
    private const string WatchFolderDefaultSelectionId = "__default__";

    private readonly IFileTranscriptionProcessor _processor;
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly WatchFolderService _watchFolder;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private bool _isProcessingQueue;
    private bool _isLoadingFileTranscriptionSettings;
    private bool _isLoadingWatchSettings;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusText = Loc.Instance["FileTranscription.StatusDefault"];
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private FileTranscriptionQueueItemViewModel? _selectedItem;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private double _processingTime;
    [ObservableProperty] private double _audioDuration;
    [ObservableProperty] private string? _fileTranscriptionEngineOverride;
    [ObservableProperty] private string? _fileTranscriptionModelOverride;
    [ObservableProperty] private string _fileTranscriptionEngineSelection = WatchFolderDefaultSelectionId;
    [ObservableProperty] private string _fileTranscriptionModelSelection = WatchFolderDefaultSelectionId;
    [ObservableProperty] private string? _watchFolderPath;
    [ObservableProperty] private string? _watchFolderOutputPath;
    [ObservableProperty] private string _watchFolderOutputFormat = "md";
    [ObservableProperty] private bool _watchFolderAutoStart;
    [ObservableProperty] private bool _watchFolderDeleteSource;
    [ObservableProperty] private string _watchFolderLanguage = "auto";
    [ObservableProperty] private string? _watchFolderEngineOverride;
    [ObservableProperty] private string? _watchFolderModelOverride;
    [ObservableProperty] private string _watchFolderEngineSelection = WatchFolderDefaultSelectionId;
    [ObservableProperty] private string _watchFolderModelSelection = WatchFolderDefaultSelectionId;
    [ObservableProperty] private bool _isWatchFolderRunning;
    [ObservableProperty] private string? _currentlyProcessingWatchFile;

    /// <summary>
    /// Gets the items.
    /// </summary>
    public ObservableCollection<FileTranscriptionQueueItemViewModel> Items { get; } = [];
    /// <summary>
    /// Gets the watch folder output format options.
    /// </summary>
    public ObservableCollection<WatchFolderOutputFormatOption> WatchFolderOutputFormatOptions { get; } = [];
    /// <summary>
    /// Gets the watch folder language options.
    /// </summary>
    public ObservableCollection<WatchFolderLanguageOption> WatchFolderLanguageOptions { get; } = [];
    /// <summary>
    /// Gets the file transcription engine options.
    /// </summary>
    public ObservableCollection<WatchFolderEngineOption> FileTranscriptionEngineOptions { get; } = [];
    /// <summary>
    /// Gets the file transcription model options.
    /// </summary>
    public ObservableCollection<WatchFolderModelOption> FileTranscriptionModelOptions { get; } = [];
    /// <summary>
    /// Gets the watch folder engine options.
    /// </summary>
    public ObservableCollection<WatchFolderEngineOption> WatchFolderEngineOptions { get; } = [];
    /// <summary>
    /// Gets the watch folder model options.
    /// </summary>
    public ObservableCollection<WatchFolderModelOption> WatchFolderModelOptions { get; } = [];
    /// <summary>
    /// Gets the watch folder history.
    /// </summary>
    public ObservableCollection<WatchFolderHistoryItem> WatchFolderHistory { get; } = [];
    /// <summary>
    /// Gets whether has items.
    /// </summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// Gets whether the file queue contains items that can be cleared.
    /// </summary>
    public bool HasClearableItems => Items.Any(IsClearableQueueItem);

    /// <summary>
    /// Returns whether watch folder path.
    /// </summary>
    public bool HasWatchFolderPath => !string.IsNullOrWhiteSpace(WatchFolderPath);
    /// <summary>
    /// Returns whether watch folder output path.
    /// </summary>
    public bool HasWatchFolderOutputPath => !string.IsNullOrWhiteSpace(WatchFolderOutputPath);
    /// <summary>
    /// Gets whether has watch folder history.
    /// </summary>
    public bool HasWatchFolderHistory => WatchFolderHistory.Count > 0;
    /// <summary>
    /// Returns whether choose file transcription model.
    /// </summary>
    public bool CanChooseFileTranscriptionModel => !string.IsNullOrWhiteSpace(FileTranscriptionEngineOverride);
    /// <summary>
    /// Gets whether is watch folder stopped.
    /// </summary>
    public bool IsWatchFolderStopped => !IsWatchFolderRunning;
    /// <summary>
    /// Returns whether choose watch folder model.
    /// </summary>
    public bool CanChooseWatchFolderModel => !string.IsNullOrWhiteSpace(WatchFolderEngineOverride);
    /// <summary>
    /// Gets the watch folder output path display.
    /// </summary>
    public string WatchFolderOutputPathDisplay => HasWatchFolderOutputPath
        ? WatchFolderOutputPath!
        : Loc.Instance["WatchFolder.OutputSameAsWatch"];
    /// <summary>
    /// Gets the watch folder status text.
    /// </summary>
    public string WatchFolderStatusText
    {
        get
        {
            if (IsWatchFolderRunning && !string.IsNullOrWhiteSpace(CurrentlyProcessingWatchFile))
                return Loc.Instance.GetString("WatchFolder.ProcessingFormat", CurrentlyProcessingWatchFile);

            return IsWatchFolderRunning
                ? Loc.Instance["WatchFolder.StatusWatching"]
                : Loc.Instance["WatchFolder.StatusStopped"];
        }
    }

    /// <summary>
    /// Initializes a new instance of the FileTranscriptionViewModel class.
    /// </summary>
    public FileTranscriptionViewModel(
        IFileTranscriptionProcessor processor,
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        WatchFolderService watchFolder)
    {
        _processor = processor;
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _watchFolder = watchFolder;

        Initialize(subscribeModelManager: true);
    }

    internal FileTranscriptionViewModel(
        IFileTranscriptionProcessor processor,
        ISettingsService settings,
        WatchFolderService watchFolder)
    {
        _processor = processor;
        _modelManager = null!;
        _settings = settings;
        _audioFile = null!;
        _dictionary = null!;
        _vocabularyBoosting = null!;
        _pipeline = null!;
        _watchFolder = watchFolder;

        Initialize(subscribeModelManager: false);
    }

    private void Initialize(bool subscribeModelManager)
    {
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            RefreshQueueCommandState();
            RefreshStatusText();
        };

        RefreshWatchFolderOptionLists();
        LoadFileTranscriptionSettings(_settings.Current);
        LoadWatchFolderSettings(_settings.Current);
        SyncWatchFolderState();

        _watchFolder.StateChanged += (_, _) => InvokeOnUiThread(SyncWatchFolderState);
        _settings.SettingsChanged += s => InvokeOnUiThread(() =>
        {
            LoadFileTranscriptionSettings(s);
            LoadWatchFolderSettings(s);
        });
        if (subscribeModelManager && _modelManager is not null)
            _modelManager.PluginManager.PluginStateChanged += (_, _) => InvokeOnUiThread(RefreshTranscriptionEnginesAndModels);
        Loc.Instance.LanguageChanged += (_, _) => InvokeOnUiThread(OnLocalizationChanged);
    }

    [RelayCommand]
    private void AddFiles(IEnumerable<string>? paths)
    {
        if (paths is null)
            return;

        var addedSupported = false;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var status = AudioFileService.IsSupported(path)
                ? FileTranscriptionQueueItemStatus.Queued
                : FileTranscriptionQueueItemStatus.Unsupported;
            var item = new FileTranscriptionQueueItemViewModel(path, status);
            Items.Add(item);
            SelectedItem ??= item;
            addedSupported |= status == FileTranscriptionQueueItemStatus.Queued;
        }

        if (addedSupported)
            _ = ProcessQueueAsync();
    }

    [RelayCommand]
    private void TranscribeFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            AddFiles([path]);
    }

    [RelayCommand]
    private void Cancel()
    {
        foreach (var item in Items.Where(item => item.CanCancel).ToList())
            CancelItem(item);
    }

    [RelayCommand]
    private void CancelItem(FileTranscriptionQueueItemViewModel? item)
    {
        if (item is null || !item.CanCancel)
            return;

        if (item.Status == FileTranscriptionQueueItemStatus.Queued)
        {
            SetStatus(item, FileTranscriptionQueueItemStatus.Cancelled, Loc.Instance["Status.Cancelled"]);
            RefreshStatusText();
            return;
        }

        item.Cancellation?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private void ClearQueue()
    {
        var itemsToRemove = Items.Where(IsClearableQueueItem).ToList();
        if (itemsToRemove.Count == 0)
            return;

        var selectedWasRemoved = SelectedItem is not null && itemsToRemove.Contains(SelectedItem);
        foreach (var item in itemsToRemove)
            Items.Remove(item);

        if (selectedWasRemoved)
            SelectedItem = Items.FirstOrDefault();

        RefreshQueueCommandState();
        RefreshStatusText();
    }

    [RelayCommand]
    private void CopyItem(FileTranscriptionQueueItemViewModel? item)
    {
        if (item?.HasResult == true)
            Clipboard.SetText(item.ResultText);
    }

    [RelayCommand]
    private void ExportItemSrt(FileTranscriptionQueueItemViewModel? item)
    {
        if (item?.RawResult?.Segments is not { Count: > 0 })
            return;

        ExportFile(item, "srt", SubtitleExporter.ToSrt(item.RawResult.Segments));
    }

    [RelayCommand]
    private void ExportItemWebVtt(FileTranscriptionQueueItemViewModel? item)
    {
        if (item?.RawResult?.Segments is not { Count: > 0 })
            return;

        ExportFile(item, "vtt", SubtitleExporter.ToWebVtt(item.RawResult.Segments));
    }

    [RelayCommand]
    private void ExportItemText(FileTranscriptionQueueItemViewModel? item)
    {
        if (item?.HasResult != true)
            return;

        ExportFile(item, "txt", item.ResultText);
    }

    /// <summary>
    /// Performs handle file drop.
    /// </summary>
    public void HandleFileDrop(string[] files)
    {
        AddFiles(files);
    }

    private async Task ProcessQueueAsync()
    {
        if (_isProcessingQueue)
            return;

        _isProcessingQueue = true;
        IsProcessing = true;

        try
        {
            while (Items.FirstOrDefault(item => item.Status == FileTranscriptionQueueItemStatus.Queued) is { } item)
            {
                SelectedItem = item;
                item.Cancellation = new CancellationTokenSource();
                var gateHeld = false;

                try
                {
                    await _transcriptionGate.WaitAsync(item.Cancellation.Token);
                    gateHeld = true;

                    var result = await _processor.ProcessAsync(
                        item.FilePath,
                        progress => SetStatus(item, progress.Status, progress.StatusText),
                        BuildFileTranscriptionOptions(),
                        item.Cancellation.Token);
                    item.RawResult = result.RawResult;
                    item.ResultText = result.ProcessedText;
                    item.DetectedLanguage = result.RawResult.DetectedLanguage;
                    item.ProcessingTime = result.RawResult.ProcessingTime;
                    item.AudioDuration = result.RawResult.Duration;
                    item.RefreshExportState();

                    SetStatus(item, FileTranscriptionQueueItemStatus.Completed,
                        Loc.Instance.GetString("FileTranscription.DoneFormat", result.RawResult.ProcessingTime, result.RawResult.Duration));
                }
                catch (OperationCanceledException)
                {
                    SetStatus(item, FileTranscriptionQueueItemStatus.Cancelled, Loc.Instance["Status.Cancelled"]);
                }
                catch (Exception ex)
                {
                    item.ErrorText = ex.Message;
                    SetStatus(item, FileTranscriptionQueueItemStatus.Error,
                        Loc.Instance.GetString("Status.ErrorFormat", ex.Message));
                }
                finally
                {
                    if (gateHeld)
                        _transcriptionGate.Release();

                    item.Cancellation?.Dispose();
                    item.Cancellation = null;
                }
            }
        }
        finally
        {
            _isProcessingQueue = false;
            IsProcessing = Items.Any(item => item.IsProcessing);
            RefreshStatusText();
        }
    }

    private FileTranscriptionProcessOptions BuildFileTranscriptionOptions()
    {
        var s = _settings.Current;
        var language = s.Language == "auto" ? null : s.Language;
        var task = s.TranscriptionTask == "translate"
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;

        return new FileTranscriptionProcessOptions(
            CleanSettingValue(FileTranscriptionEngineOverride),
            CleanSettingValue(FileTranscriptionModelOverride),
            language,
            task);
    }

    private void SetStatus(FileTranscriptionQueueItemViewModel item, FileTranscriptionQueueItemStatus status, string statusText)
    {
        item.Status = status;
        item.StatusText = statusText;
        RefreshStatusText();
        RefreshQueueCommandState();
    }

    private bool CanClearQueue() => HasClearableItems;

    private void RefreshQueueCommandState()
    {
        OnPropertyChanged(nameof(HasClearableItems));
        ClearQueueCommand.NotifyCanExecuteChanged();
    }

    private static bool IsClearableQueueItem(FileTranscriptionQueueItemViewModel item) =>
        item.Status is FileTranscriptionQueueItemStatus.Completed
            or FileTranscriptionQueueItemStatus.Cancelled
            or FileTranscriptionQueueItemStatus.Error
            or FileTranscriptionQueueItemStatus.Unsupported;

    private void RefreshStatusText()
    {
        var total = Items.Count;
        if (total == 0)
        {
            StatusText = Loc.Instance["FileTranscription.StatusDefault"];
            return;
        }

        var completed = Items.Count(item => item.Status == FileTranscriptionQueueItemStatus.Completed);
        var failed = Items.Count(item => item.Status is FileTranscriptionQueueItemStatus.Error or FileTranscriptionQueueItemStatus.Unsupported);
        var cancelled = Items.Count(item => item.Status == FileTranscriptionQueueItemStatus.Cancelled);
        var queued = Items.Count(item => item.Status == FileTranscriptionQueueItemStatus.Queued);

        StatusText = Loc.Instance.GetString("FileTranscription.QueueStatusFormat", completed, failed, cancelled, queued, total);
    }

    private void ExportFile(FileTranscriptionQueueItemViewModel item, string extension, string content)
    {
        var baseName = Path.GetFileNameWithoutExtension(item.FilePath);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{baseName}.{extension}",
            Filter = extension.ToUpperInvariant() + $" Files|*.{extension}|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, content);
            item.StatusText = Loc.Instance.GetString("FileTranscription.ExportedFormat", Path.GetFileName(dialog.FileName));
            StatusText = item.StatusText;
        }
    }

    /// <summary>
    /// Sets watch folder path.
    /// </summary>
    public void SetWatchFolderPath(string path) => WatchFolderPath = path;

    /// <summary>
    /// Sets watch folder output path.
    /// </summary>
    public void SetWatchFolderOutputPath(string path) => WatchFolderOutputPath = path;

    [RelayCommand]
    private void ClearWatchFolderOutputPath()
    {
        WatchFolderOutputPath = null;
    }

    [RelayCommand]
    private void StartWatchFolder()
    {
        if (string.IsNullOrWhiteSpace(WatchFolderPath))
            return;

        _watchFolder.Start(BuildWatchFolderOptions(), TranscribeWatchFolderFileAsync);
        SyncWatchFolderState();
    }

    [RelayCommand]
    private void StopWatchFolder()
    {
        _watchFolder.Stop();
        SyncWatchFolderState();
    }

    [RelayCommand]
    private void ClearWatchFolderHistory()
    {
        _watchFolder.ClearHistory();
        SyncWatchFolderState();
    }

    /// <summary>
    /// Starts watch folder from settings.
    /// </summary>
    public void StartWatchFolderFromSettings()
    {
        if (!HasWatchFolderPath)
            return;

        StartWatchFolder();
    }

    partial void OnWatchFolderPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasWatchFolderPath));
        SaveWatchFolderSettings(restartIfRunning: true);
    }

    partial void OnWatchFolderOutputPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasWatchFolderOutputPath));
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
        SaveWatchFolderSettings(restartIfRunning: true);
    }

    partial void OnWatchFolderOutputFormatChanged(string value) =>
        SaveWatchFolderSettings(restartIfRunning: true);

    partial void OnWatchFolderAutoStartChanged(bool value) =>
        SaveWatchFolderSettings(restartIfRunning: false);

    partial void OnWatchFolderDeleteSourceChanged(bool value) =>
        SaveWatchFolderSettings(restartIfRunning: true);

    partial void OnWatchFolderLanguageChanged(string value) =>
        SaveWatchFolderSettings(restartIfRunning: false);

    partial void OnFileTranscriptionEngineOverrideChanged(string? value)
    {
        var selection = SettingToSelection(value);
        if (FileTranscriptionEngineSelection != selection)
            FileTranscriptionEngineSelection = selection;

        RebuildFileTranscriptionModels();
        if (!string.IsNullOrWhiteSpace(FileTranscriptionModelOverride)
            && !FileTranscriptionModelOptions.Any(m => m.Id == FileTranscriptionModelOverride))
        {
            FileTranscriptionModelOverride = null;
        }

        OnPropertyChanged(nameof(CanChooseFileTranscriptionModel));
        SaveFileTranscriptionSettings();
    }

    partial void OnFileTranscriptionModelOverrideChanged(string? value)
    {
        var selection = SettingToSelection(value);
        if (FileTranscriptionModelSelection != selection)
            FileTranscriptionModelSelection = selection;

        SaveFileTranscriptionSettings();
    }

    partial void OnFileTranscriptionEngineSelectionChanged(string value)
    {
        var setting = SelectionToSetting(value);
        if (FileTranscriptionEngineOverride != setting)
            FileTranscriptionEngineOverride = setting;
    }

    partial void OnFileTranscriptionModelSelectionChanged(string value)
    {
        var setting = SelectionToSetting(value);
        if (FileTranscriptionModelOverride != setting)
            FileTranscriptionModelOverride = setting;
    }

    partial void OnWatchFolderEngineOverrideChanged(string? value)
    {
        var selection = SettingToSelection(value);
        if (WatchFolderEngineSelection != selection)
            WatchFolderEngineSelection = selection;

        RebuildWatchFolderModels();
        if (!string.IsNullOrWhiteSpace(WatchFolderModelOverride)
            && !WatchFolderModelOptions.Any(m => m.Id == WatchFolderModelOverride))
        {
            WatchFolderModelOverride = null;
        }

        OnPropertyChanged(nameof(CanChooseWatchFolderModel));
        SaveWatchFolderSettings(restartIfRunning: false);
    }

    partial void OnWatchFolderModelOverrideChanged(string? value)
    {
        var selection = SettingToSelection(value);
        if (WatchFolderModelSelection != selection)
            WatchFolderModelSelection = selection;

        SaveWatchFolderSettings(restartIfRunning: false);
    }

    partial void OnWatchFolderEngineSelectionChanged(string value)
    {
        var setting = SelectionToSetting(value);
        if (WatchFolderEngineOverride != setting)
            WatchFolderEngineOverride = setting;
    }

    partial void OnWatchFolderModelSelectionChanged(string value)
    {
        var setting = SelectionToSetting(value);
        if (WatchFolderModelOverride != setting)
            WatchFolderModelOverride = setting;
    }

    partial void OnIsWatchFolderRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWatchFolderStopped));
        OnPropertyChanged(nameof(WatchFolderStatusText));
    }

    private Func<string, string>? GetVocabularyBooster() =>
        _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null;

    private async Task<WatchFolderTranscriptionResult> TranscribeWatchFolderFileAsync(
        WatchFolderTranscriptionRequest request,
        CancellationToken ct)
    {
        await _transcriptionGate.WaitAsync(ct);
        try
        {
            var engine = CleanSettingValue(WatchFolderEngineOverride);
            var model = CleanSettingValue(WatchFolderModelOverride);

            await using var modelScope = await _modelManager.BeginTranscriptionRequestAsync(engine, model, false, ct);
            var samples = await _audioFile.LoadAudioAsync(request.FilePath, ct);
            var language = WatchFolderLanguage == "auto" ? null : CleanSettingValue(WatchFolderLanguage);
            var activeResult = await _modelManager.TranscribeActiveAsync(
                samples,
                language,
                TranscriptionTask.Transcribe,
                prompt: null,
                cancellationToken: ct);

            var result = activeResult.Result;
            var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
            {
                VocabularyBooster = GetVocabularyBooster(),
                DictionaryCorrector = _dictionary.ApplyCorrections
            }, ct);

            _modelManager.ScheduleAutoUnload();

            return new WatchFolderTranscriptionResult(
                pipelineResult.Text,
                result.DetectedLanguage,
                result.Duration,
                result.ProcessingTime,
                result.Segments,
                activeResult.EngineId,
                activeResult.ModelId);
        }
        finally
        {
            _transcriptionGate.Release();
        }
    }

    private WatchFolderOptions BuildWatchFolderOptions() =>
        new(
            WatchFolderPath!,
            CleanSettingValue(WatchFolderOutputPath),
            WatchFolderOutputFormats.Parse(WatchFolderOutputFormat),
            WatchFolderDeleteSource);

    private void RestartWatchFolderIfRunning()
    {
        if (!_watchFolder.IsRunning || string.IsNullOrWhiteSpace(WatchFolderPath))
            return;

        _watchFolder.Start(BuildWatchFolderOptions(), TranscribeWatchFolderFileAsync);
        SyncWatchFolderState();
    }

    private void SaveFileTranscriptionSettings()
    {
        if (_isLoadingFileTranscriptionSettings)
            return;

        _settings.Save(_settings.Current with
        {
            FileTranscriptionEngineOverride = CleanSettingValue(FileTranscriptionEngineOverride),
            FileTranscriptionModelOverride = CleanSettingValue(FileTranscriptionModelOverride)
        });
    }

    private void SaveWatchFolderSettings(bool restartIfRunning)
    {
        if (_isLoadingWatchSettings)
            return;

        _settings.Save(_settings.Current with
        {
            WatchFolderPath = CleanSettingValue(WatchFolderPath),
            WatchFolderOutputPath = CleanSettingValue(WatchFolderOutputPath),
            WatchFolderOutputFormat = string.IsNullOrWhiteSpace(WatchFolderOutputFormat) ? "md" : WatchFolderOutputFormat,
            WatchFolderAutoStart = WatchFolderAutoStart,
            WatchFolderDeleteSource = WatchFolderDeleteSource,
            WatchFolderLanguage = string.IsNullOrWhiteSpace(WatchFolderLanguage) ? "auto" : WatchFolderLanguage,
            WatchFolderEngineOverride = CleanSettingValue(WatchFolderEngineOverride),
            WatchFolderModelOverride = CleanSettingValue(WatchFolderModelOverride)
        });

        if (restartIfRunning)
            RestartWatchFolderIfRunning();
    }

    private void LoadFileTranscriptionSettings(AppSettings s)
    {
        _isLoadingFileTranscriptionSettings = true;
        FileTranscriptionEngineOverride = s.FileTranscriptionEngineOverride;
        FileTranscriptionModelOverride = s.FileTranscriptionModelOverride;
        FileTranscriptionEngineSelection = SettingToSelection(s.FileTranscriptionEngineOverride);
        FileTranscriptionModelSelection = SettingToSelection(s.FileTranscriptionModelOverride);
        _isLoadingFileTranscriptionSettings = false;

        RefreshFileTranscriptionEnginesAndModels();
        OnPropertyChanged(nameof(CanChooseFileTranscriptionModel));
    }

    private void LoadWatchFolderSettings(AppSettings s)
    {
        _isLoadingWatchSettings = true;
        WatchFolderPath = s.WatchFolderPath;
        WatchFolderOutputPath = s.WatchFolderOutputPath;
        WatchFolderOutputFormat = string.IsNullOrWhiteSpace(s.WatchFolderOutputFormat) ? "md" : s.WatchFolderOutputFormat;
        WatchFolderAutoStart = s.WatchFolderAutoStart;
        WatchFolderDeleteSource = s.WatchFolderDeleteSource;
        WatchFolderLanguage = string.IsNullOrWhiteSpace(s.WatchFolderLanguage) ? "auto" : s.WatchFolderLanguage;
        WatchFolderEngineOverride = s.WatchFolderEngineOverride;
        WatchFolderModelOverride = s.WatchFolderModelOverride;
        WatchFolderEngineSelection = SettingToSelection(s.WatchFolderEngineOverride);
        WatchFolderModelSelection = SettingToSelection(s.WatchFolderModelOverride);
        _isLoadingWatchSettings = false;

        RefreshWatchFolderEnginesAndModels();
        OnPropertyChanged(nameof(HasWatchFolderPath));
        OnPropertyChanged(nameof(HasWatchFolderOutputPath));
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
        OnPropertyChanged(nameof(CanChooseWatchFolderModel));
    }

    private void OnLocalizationChanged()
    {
        RefreshWatchFolderOptionLists();
        RefreshFileTranscriptionEnginesAndModels();
        OnPropertyChanged(nameof(WatchFolderOutputPathDisplay));
        OnPropertyChanged(nameof(WatchFolderStatusText));
    }

    private void RefreshWatchFolderOptionLists()
    {
        ReplaceCollection(WatchFolderOutputFormatOptions, [
            new("md", Loc.Instance["WatchFolder.FormatMarkdown"]),
            new("txt", Loc.Instance["WatchFolder.FormatText"]),
            new("srt", "SRT"),
            new("vtt", "VTT")
        ]);

        ReplaceCollection(WatchFolderLanguageOptions, [
            new("auto", Loc.Instance["Profiles.Auto"]),
            new("de", "Deutsch"),
            new("en", "English"),
            new("fr", "Francais"),
            new("es", "Espanol")
        ]);

        RefreshWatchFolderEnginesAndModels();
    }

    private void RefreshTranscriptionEnginesAndModels()
    {
        RefreshFileTranscriptionEnginesAndModels();
        RefreshWatchFolderEnginesAndModels();
    }

    private void RefreshFileTranscriptionEnginesAndModels()
    {
        var options = new List<WatchFolderEngineOption>
        {
            new(WatchFolderDefaultSelectionId, Loc.Instance["FileTranscription.DefaultEngine"])
        };

        if (_modelManager is null)
        {
            ReplaceCollection(FileTranscriptionEngineOptions, options);
            RebuildFileTranscriptionModels();
            return;
        }

        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines
            .DistinctBy(engine => engine.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = engine.IsConfigured
                ? engine.ProviderDisplayName
                : Loc.Instance.GetString("WatchFolder.EngineNotReadyFormat", engine.ProviderDisplayName);
            options.Add(new(engine.ProviderId, displayName));
        }

        ReplaceCollection(FileTranscriptionEngineOptions, options);
        RebuildFileTranscriptionModels();
    }

    private void RebuildFileTranscriptionModels()
    {
        var options = new List<WatchFolderModelOption>
        {
            new(WatchFolderDefaultSelectionId, Loc.Instance["FileTranscription.DefaultModel"])
        };

        if (_modelManager is null)
        {
            ReplaceCollection(FileTranscriptionModelOptions, options);
            return;
        }

        var engine = string.IsNullOrWhiteSpace(FileTranscriptionEngineOverride)
            ? null
            : _modelManager.PluginManager.TranscriptionEngines
                .DistinctBy(e => e.ProviderId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(e => e.ProviderId.Equals(FileTranscriptionEngineOverride, StringComparison.OrdinalIgnoreCase));

        if (engine is not null)
        {
            options.AddRange(engine.TranscriptionModels
                .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new WatchFolderModelOption(model.Id, model.DisplayName)));
        }

        ReplaceCollection(FileTranscriptionModelOptions, options);
    }

    private void RefreshWatchFolderEnginesAndModels()
    {
        var options = new List<WatchFolderEngineOption>
        {
            new(WatchFolderDefaultSelectionId, Loc.Instance["WatchFolder.DefaultEngine"])
        };

        if (_modelManager is null)
        {
            ReplaceCollection(WatchFolderEngineOptions, options);
            RebuildWatchFolderModels();
            return;
        }

        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines
            .DistinctBy(engine => engine.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = engine.IsConfigured
                ? engine.ProviderDisplayName
                : Loc.Instance.GetString("WatchFolder.EngineNotReadyFormat", engine.ProviderDisplayName);
            options.Add(new(engine.ProviderId, displayName));
        }

        ReplaceCollection(WatchFolderEngineOptions, options);
        RebuildWatchFolderModels();
    }

    private void RebuildWatchFolderModels()
    {
        var options = new List<WatchFolderModelOption>
        {
            new(WatchFolderDefaultSelectionId, Loc.Instance["WatchFolder.DefaultModel"])
        };

        if (_modelManager is null)
        {
            ReplaceCollection(WatchFolderModelOptions, options);
            return;
        }

        var engine = string.IsNullOrWhiteSpace(WatchFolderEngineOverride)
            ? null
            : _modelManager.PluginManager.TranscriptionEngines
                .DistinctBy(e => e.ProviderId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(e => e.ProviderId.Equals(WatchFolderEngineOverride, StringComparison.OrdinalIgnoreCase));

        if (engine is not null)
        {
            options.AddRange(engine.TranscriptionModels
                .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(model => new WatchFolderModelOption(model.Id, model.DisplayName)));
        }

        ReplaceCollection(WatchFolderModelOptions, options);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var snapshot = items.ToList();
        if (target.SequenceEqual(snapshot))
            return;

        target.Clear();
        foreach (var item in snapshot)
            target.Add(item);
    }

    private void SyncWatchFolderState()
    {
        IsWatchFolderRunning = _watchFolder.IsRunning;
        CurrentlyProcessingWatchFile = _watchFolder.CurrentlyProcessing;
        WatchFolderHistory.Clear();
        foreach (var item in _watchFolder.History)
            WatchFolderHistory.Add(item);

        OnPropertyChanged(nameof(WatchFolderStatusText));
        OnPropertyChanged(nameof(HasWatchFolderHistory));
        StartWatchFolderCommand.NotifyCanExecuteChanged();
        StopWatchFolderCommand.NotifyCanExecuteChanged();
    }

    private static string? CleanSettingValue(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string SettingToSelection(string? value) =>
        string.IsNullOrWhiteSpace(value) ? WatchFolderDefaultSelectionId : value.Trim();

    private static string? SelectionToSetting(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == WatchFolderDefaultSelectionId
            ? null
            : value.Trim();

    private static void InvokeOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
            return;
        }

        action();
    }
}

/// <summary>
/// Represents watch folder output format option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record WatchFolderOutputFormatOption(string Id, string DisplayName);
/// <summary>
/// Represents watch folder language option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record WatchFolderLanguageOption(string Id, string DisplayName);
/// <summary>
/// Represents watch folder engine option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record WatchFolderEngineOption(string Id, string DisplayName);
/// <summary>
/// Represents watch folder model option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record WatchFolderModelOption(string Id, string DisplayName);
