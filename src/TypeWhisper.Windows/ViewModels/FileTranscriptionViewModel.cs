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

public partial class FileTranscriptionViewModel : ObservableObject
{
    private const string WatchFolderDefaultSelectionId = "__default__";

    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly WatchFolderService _watchFolder;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private bool _isLoadingFileTranscriptionSettings;
    private bool _isLoadingWatchSettings;

    private CancellationTokenSource? _cts;
    private TranscriptionResult? _lastResult;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusText = Loc.Instance["FileTranscription.StatusDefault"];
    [ObservableProperty] private bool _isProcessing;
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

    public ObservableCollection<WatchFolderOutputFormatOption> WatchFolderOutputFormatOptions { get; } = [];
    public ObservableCollection<WatchFolderLanguageOption> WatchFolderLanguageOptions { get; } = [];
    public ObservableCollection<WatchFolderEngineOption> FileTranscriptionEngineOptions { get; } = [];
    public ObservableCollection<WatchFolderModelOption> FileTranscriptionModelOptions { get; } = [];
    public ObservableCollection<WatchFolderEngineOption> WatchFolderEngineOptions { get; } = [];
    public ObservableCollection<WatchFolderModelOption> WatchFolderModelOptions { get; } = [];
    public ObservableCollection<WatchFolderHistoryItem> WatchFolderHistory { get; } = [];
    public bool HasWatchFolderPath => !string.IsNullOrWhiteSpace(WatchFolderPath);
    public bool HasWatchFolderOutputPath => !string.IsNullOrWhiteSpace(WatchFolderOutputPath);
    public bool HasWatchFolderHistory => WatchFolderHistory.Count > 0;
    public bool CanChooseFileTranscriptionModel => !string.IsNullOrWhiteSpace(FileTranscriptionEngineOverride);
    public bool IsWatchFolderStopped => !IsWatchFolderRunning;
    public bool CanChooseWatchFolderModel => !string.IsNullOrWhiteSpace(WatchFolderEngineOverride);
    public bool CanExportSubtitles => HasResult && _lastResult?.Segments is { Count: > 0 };
    public string WatchFolderOutputPathDisplay => HasWatchFolderOutputPath
        ? WatchFolderOutputPath!
        : Loc.Instance["WatchFolder.OutputSameAsWatch"];
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

    public FileTranscriptionViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        WatchFolderService watchFolder)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _watchFolder = watchFolder;

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
        _modelManager.PluginManager.PluginStateChanged += (_, _) => InvokeOnUiThread(RefreshTranscriptionEnginesAndModels);
        Loc.Instance.LanguageChanged += (_, _) => InvokeOnUiThread(OnLocalizationChanged);
    }

    [RelayCommand]
    private async Task TranscribeFile(string? path)
    {
        var filePath = path ?? FilePath;
        if (string.IsNullOrEmpty(filePath)) return;

        if (!AudioFileService.IsSupported(filePath))
        {
            StatusText = Loc.Instance["FileTranscription.UnsupportedFormat"];
            return;
        }

        FilePath = filePath;
        IsProcessing = true;
        HasResult = false;
        SetLastResult(null);
        ResultText = "";
        StatusText = Loc.Instance["FileTranscription.LoadingAudio"];

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var gateHeld = false;

        try
        {
            await _transcriptionGate.WaitAsync(_cts.Token);
            gateHeld = true;

            var engine = CleanSettingValue(FileTranscriptionEngineOverride);
            var model = CleanSettingValue(FileTranscriptionModelOverride);
            await using var modelScope = await _modelManager.BeginTranscriptionRequestAsync(engine, model, false, _cts.Token);

            var samples = await _audioFile.LoadAudioAsync(filePath, _cts.Token);

            StatusText = Loc.Instance["FileTranscription.Transcribing"];

            var s = _settings.Current;
            var language = s.Language == "auto" ? null : s.Language;
            var task = s.TranscriptionTask == "translate"
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;

            var activeResult = await _modelManager.TranscribeActiveAsync(
                samples,
                language,
                task,
                prompt: null,
                cancellationToken: _cts.Token);
            var result = activeResult.Result;
            var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
            {
                VocabularyBooster = GetVocabularyBooster(),
                DictionaryCorrector = _dictionary.ApplyCorrections
            }, _cts.Token);

            _modelManager.ScheduleAutoUnload();
            SetLastResult(result);

            ResultText = pipelineResult.Text;
            DetectedLanguage = result.DetectedLanguage;
            ProcessingTime = result.ProcessingTime;
            AudioDuration = result.Duration;
            HasResult = true;
            StatusText = Loc.Instance.GetString("FileTranscription.DoneFormat", result.ProcessingTime, result.Duration);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Instance["Status.Cancelled"];
        }
        catch (Exception ex)
        {
            StatusText = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
        }
        finally
        {
            if (gateHeld)
                _transcriptionGate.Release();

            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(ResultText))
            System.Windows.Clipboard.SetText(ResultText);
    }

    [RelayCommand(CanExecute = nameof(CanExportSubtitles))]
    private void ExportSrt()
    {
        if (_lastResult?.Segments is not { Count: > 0 })
        {
            StatusText = Loc.Instance["FileTranscription.SubtitleUnavailable"];
            return;
        }

        ExportFile("srt", SubtitleExporter.ToSrt(_lastResult.Segments));
    }

    [RelayCommand(CanExecute = nameof(CanExportSubtitles))]
    private void ExportWebVtt()
    {
        if (_lastResult?.Segments is not { Count: > 0 })
        {
            StatusText = Loc.Instance["FileTranscription.SubtitleUnavailable"];
            return;
        }

        ExportFile("vtt", SubtitleExporter.ToWebVtt(_lastResult.Segments));
    }

    [RelayCommand]
    private void ExportText()
    {
        if (string.IsNullOrEmpty(ResultText)) return;
        ExportFile("txt", ResultText);
    }

    private void ExportFile(string extension, string content)
    {
        var baseName = FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath) : "transcription";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{baseName}.{extension}",
            Filter = extension.ToUpperInvariant() + $" Files|*.{extension}|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, content);
            StatusText = Loc.Instance.GetString("FileTranscription.ExportedFormat", Path.GetFileName(dialog.FileName));
        }
    }

    private void SetLastResult(TranscriptionResult? result)
    {
        _lastResult = result;
        OnPropertyChanged(nameof(CanExportSubtitles));
        ExportSrtCommand.NotifyCanExecuteChanged();
        ExportWebVttCommand.NotifyCanExecuteChanged();
    }

    public void HandleFileDrop(string[] files)
    {
        if (files.Length > 0 && AudioFileService.IsSupported(files[0]))
        {
            TranscribeFileCommand.Execute(files[0]);
        }
    }

    public void SetWatchFolderPath(string path) => WatchFolderPath = path;

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

    partial void OnHasResultChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExportSubtitles));
        ExportSrtCommand.NotifyCanExecuteChanged();
        ExportWebVttCommand.NotifyCanExecuteChanged();
    }

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

public sealed record WatchFolderOutputFormatOption(string Id, string DisplayName);
public sealed record WatchFolderLanguageOption(string Id, string DisplayName);
public sealed record WatchFolderEngineOption(string Id, string DisplayName);
public sealed record WatchFolderModelOption(string Id, string DisplayName);
