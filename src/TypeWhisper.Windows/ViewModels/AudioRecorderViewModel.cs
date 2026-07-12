using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Represents recording item data.
/// </summary>
/// <param name="FileName">File name supplied to the member.</param>
/// <param name="FilePath">File path supplied to the member.</param>
/// <param name="CreatedAt">Created at supplied to the member.</param>
/// <param name="Duration">Duration supplied to the member.</param>
/// <param name="Transcript">Transcript supplied to the member.</param>
/// <param name="IsTranscribing">Transcription progress flag supplied to the member.</param>
/// <param name="ErrorMessage">Failure message supplied to the member.</param>
public sealed record RecordingItem(
    string FileName,
    string FilePath,
    DateTime CreatedAt,
    TimeSpan Duration,
    string? Transcript,
    bool IsTranscribing = false,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Gets whether a per-recording status line should be shown.
    /// </summary>
    public bool HasStatus => IsTranscribing || HasError;
    /// <summary>
    /// Gets whether this recording has a transcription failure.
    /// </summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    /// <summary>
    /// Gets the per-recording status text.
    /// </summary>
    public string ItemStatusText => IsTranscribing
        ? Loc.Instance["Recorder.TranscribingItem"]
        : ErrorMessage ?? "";
    /// <summary>
    /// Gets whether this recording can be transcribed.
    /// </summary>
    public bool CanTranscribe => !IsTranscribing && File.Exists(FilePath);
    /// <summary>
    /// Gets the tooltip for the transcription command.
    /// </summary>
    public string TranscribeToolTip => HasError
        ? Loc.Instance["Recorder.Retry"]
        : Loc.Instance["Recorder.Transcribe"];
}

/// <summary>
/// Provides audio recorder view model behavior.
/// </summary>
public partial class AudioRecorderViewModel : ObservableObject, IRecorderApiController, IDisposable
{
    private readonly RecorderCaptureService _capture;
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IErrorLogService _errorLog;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly ITranslationService _translation;
    private readonly StreamingHandler _streamingHandler;
    private readonly Dictionary<Guid, RecorderApiSessionSnapshot> _apiSessions = new();
    private readonly object _apiSessionLock = new();
    private System.Timers.Timer? _timer;
    private DateTime _recordingStart;
    private string? _statusKey;
    private bool _isLoadingSettings;
    private Guid? _activeApiSessionId;
    private bool _disposed;

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private float _systemLevel;
    [ObservableProperty] private string _statusText = Loc.Instance["Status.Ready"];
    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private bool _micEnabled = true;
    [ObservableProperty] private bool _systemAudioEnabled;
    [ObservableProperty] private SystemAudioOutputDevice? _selectedSystemAudioDevice;
    [ObservableProperty] private RecorderOutputFormat _outputFormat = RecorderOutputFormat.Wav;
    [ObservableProperty] private RecorderTrackMode _trackMode = RecorderTrackMode.Mixed;
    [ObservableProperty] private RecorderMicDuckingMode _micDuckingMode = RecorderMicDuckingMode.Aggressive;
    [ObservableProperty] private bool _transcriptionEnabled = true;
    [ObservableProperty] private bool _translationModeEnabled;
    [ObservableProperty] private string? _translationTargetLanguage;
    [ObservableProperty] private string? _transcriptionEngineOverride;
    [ObservableProperty] private string? _transcriptionModelOverride;
    [ObservableProperty] private string _partialText = "";
    [ObservableProperty] private string? _systemAudioWarningMessage;

    /// <summary>
    /// Gets the recordings.
    /// </summary>
    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    /// <summary>
    /// Gets available system-audio output devices.
    /// </summary>
    public ObservableCollection<SystemAudioOutputDevice> SystemAudioDevices { get; } = [];
    /// <summary>
    /// Gets whether has recordings.
    /// </summary>
    public bool HasRecordings => Recordings.Count > 0;
    /// <summary>
    /// Gets whether the system audio warning is visible.
    /// </summary>
    public bool HasSystemAudioWarning => !string.IsNullOrWhiteSpace(SystemAudioWarningMessage);
    /// <summary>
    /// Gets output format choices.
    /// </summary>
    public IReadOnlyList<RecorderOutputFormat> OutputFormats { get; } =
        [RecorderOutputFormat.Wav, RecorderOutputFormat.M4A];
    /// <summary>
    /// Gets track mode choices.
    /// </summary>
    public IReadOnlyList<RecorderTrackMode> TrackModes { get; } =
        [RecorderTrackMode.Mixed, RecorderTrackMode.Separate];
    /// <summary>
    /// Gets microphone ducking choices.
    /// </summary>
    public IReadOnlyList<RecorderMicDuckingMode> MicDuckingModes { get; } =
        [RecorderMicDuckingMode.Aggressive, RecorderMicDuckingMode.Medium, RecorderMicDuckingMode.Off];

    partial void OnSystemAudioWarningMessageChanged(string? value) =>
        OnPropertyChanged(nameof(HasSystemAudioWarning));

    partial void OnTranslationModeEnabledChanged(bool value)
    {
        if (_isLoadingSettings)
            return;

        if (value)
        {
            if (string.IsNullOrWhiteSpace(TranslationTargetLanguage))
                TranslationTargetLanguage = DefaultRecorderTranslationTargetLanguage();
            return;
        }

        if (!string.IsNullOrWhiteSpace(TranslationTargetLanguage))
            TranslationTargetLanguage = null;
    }

    partial void OnTranslationTargetLanguageChanged(string? value)
    {
        if (_isLoadingSettings)
            return;

        var enabled = !string.IsNullOrWhiteSpace(value);
        if (TranslationModeEnabled == enabled)
            return;

        var wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        TranslationModeEnabled = enabled;
        _isLoadingSettings = wasLoading;
    }

    /// <summary>
    /// Initializes a new instance of the AudioRecorderViewModel class.
    /// </summary>
    internal AudioRecorderViewModel(
        AudioRecordingService audio,
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IErrorLogService errorLog,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        IDictionaryService? dictionary = null)
        : this(
            new RecorderCaptureService(audio, new SystemAudioCaptureService()),
            modelManager,
            settings,
            audioFile,
            errorLog,
            pipeline,
            translation,
            dictionary)
    {
    }

    /// <summary>
    /// Initializes a new instance of the AudioRecorderViewModel class.
    /// </summary>
    public AudioRecorderViewModel(
        RecorderCaptureService capture,
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IErrorLogService errorLog,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        IDictionaryService? dictionary = null)
    {
        _capture = capture;
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _errorLog = errorLog;
        _pipeline = pipeline;
        _translation = translation;
        _streamingHandler = new StreamingHandler(
            modelManager,
            capture,
            dictionary ?? NoopDictionaryService.Instance);
        _streamingHandler.OnPartialTextUpdate = text =>
            DispatchToUi(() => PartialText = text);
        _capture.AudioLevelChanged += (_, e) =>
            DispatchToUi(() =>
            {
                AudioLevel = e.PeakLevel;
                MicLevel = _capture.MicLevel;
                SystemLevel = _capture.SystemLevel;
                SystemAudioWarningMessage = _capture.SystemAudioWarningMessage;
            });
        _statusKey = "Status.Ready";
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        LoadRecorderSettings(_settings.Current);
        _settings.SettingsChanged += OnSettingsChanged;
        PropertyChanged += (_, args) =>
        {
            if (_isLoadingSettings)
                return;

            if (IsRecorderSettingProperty(args.PropertyName))
                SaveRecorderSettings();
        };
        LoadExistingRecordings();
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    private async void StartRecording() =>
        await StartRecordingCoreAsync(MicEnabled, SystemAudioEnabled, CancellationToken.None);

    private async Task<bool> StartRecordingCoreAsync(
        bool micEnabled,
        bool systemAudioEnabled,
        CancellationToken ct)
    {
        if (!micEnabled && !systemAudioEnabled)
        {
            SetLocalizedStatus("Recorder.SelectAtLeastOneSource");
            return false;
        }

        try
        {
            await _capture.StartAsync(
                new RecorderCaptureOptions(
                    micEnabled,
                    systemAudioEnabled,
                    SelectedSystemAudioDevice?.Id,
                    OutputFormat,
                    TrackMode,
                    MicDuckingMode),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            SetErrorStatus(ex.Message);
            return false;
        }

        IsRecording = true;
        PartialText = "";
        _recordingStart = DateTime.UtcNow;
        SetLocalizedStatus("Status.Recording");

        _timer = new System.Timers.Timer(100);
        _timer.Elapsed += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - _recordingStart;
            DispatchToUi(() =>
            {
                DurationText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
                RecordingSeconds = elapsed.TotalSeconds;
                MicLevel = _capture.MicLevel;
                SystemLevel = _capture.SystemLevel;
                AudioLevel = Math.Max(MicLevel, SystemLevel);
                SystemAudioWarningMessage = _capture.SystemAudioWarningMessage;
            });
        };
        _timer.Start();

        if (TranscriptionEnabled && !string.IsNullOrWhiteSpace(_settings.Current.SelectedModelId))
        {
            _streamingHandler.StartWithLanguageHints(
                _settings.Current.GetLanguageHints(), CurrentRecorderTask, () => IsRecording);
        }

        return true;
    }

    private async void StopRecording() =>
        await StopRecordingCoreAsync(CancellationToken.None);

    private async Task<RecordingItem?> StopRecordingCoreAsync(CancellationToken ct)
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        SetLocalizedStatus("Recorder.Finalizing");
        MarkActiveApiSession(RecorderSessionStatus.Finalizing);
        var liveTranscript = _streamingHandler.Stop();

        var capture = await _capture.StopAsync(ct);
        IsRecording = false;
        RecordingSeconds = 0;
        MicLevel = 0;
        SystemLevel = 0;
        AudioLevel = 0;
        SystemAudioWarningMessage = null;

        if (capture is null || capture.TranscriptionSamples.Length == 0)
        {
            SetLocalizedStatus("Recorder.NoAudioCaptured");
            FailActiveApiSession(StatusText);
            return null;
        }

        var item = new RecordingItem(
            capture.FileName,
            capture.FilePath,
            DateTime.Now,
            capture.Duration,
            null,
            IsTranscribing: TranscriptionEnabled);
        DispatchToUi(() =>
        {
            Recordings.Insert(0, item);
            OnPropertyChanged(nameof(HasRecordings));
            RefreshRecordingCommandState();
        });

        if (!TranscriptionEnabled)
        {
            SetLocalizedStatus("Status.Done");
            CompleteActiveApiSession(null, capture.FilePath);
            DurationText = "0:00";
            return item;
        }

        var transcript = await TranscribeRecordingAsync(
            item,
            _ => Task.FromResult(capture.TranscriptionSamples),
            ct,
            liveTranscript);
        CompleteActiveApiSession(transcript, capture.FilePath);
        DurationText = "0:00";
        return item;
    }

    private async Task<string> PostProcessTranscriptAsync(
        TranscriptionResult result,
        TranscriptionTask task,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageHints,
        string? translationTarget,
        CancellationToken ct)
    {
        var currentSettings = _settings.Current;
        var shouldTranslate = task == TranscriptionTask.Translate && !string.IsNullOrEmpty(translationTarget);
        var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = currentSettings.TranscriptionNumberNormalizationEnabled,
            TranscriptionTask = task,
            ConfiguredLanguage = configuredLanguage,
            ConfiguredLanguageCandidates = configuredLanguageHints,
            TranslationHandler = shouldTranslate
                ? (text, src, tgt, token) => _translation.TranslateAsync(text, src, tgt, token)
                : null,
            TranslationTarget = translationTarget,
            RequireTranslationSuccess = shouldTranslate,
            DetectedLanguage = result.DetectedLanguage
        }, ct);

        return pipelineResult.Text;
    }

    [RelayCommand(CanExecute = nameof(CanTranscribeRecording))]
    private async Task TranscribeRecordingAsync(RecordingItem? item)
    {
        if (item is null)
            return;

        await TranscribeRecordingAsync(
            item,
            token => _audioFile.LoadAudioAsync(item.FilePath, token),
            CancellationToken.None);
    }

    private static bool CanTranscribeRecording(RecordingItem? item) =>
        item?.CanTranscribe == true;

    private async Task<string?> TranscribeRecordingAsync(
        RecordingItem item,
        Func<CancellationToken, Task<float[]>> loadSamples,
        CancellationToken ct,
        string? liveTranscript = null)
    {
        var current = ReplaceRecording(item, item with { IsTranscribing = true, ErrorMessage = null });
        SetLocalizedStatus("Recorder.SavedTranscribing");

        try
        {
            var settings = _settings.Current;
            var engineOverride = FirstNonBlank(TranscriptionEngineOverride, settings.RecorderTranscriptionEngineOverride);
            var modelOverride = FirstNonBlank(TranscriptionModelOverride, settings.RecorderTranscriptionModelOverride);
            if (string.IsNullOrWhiteSpace(settings.SelectedModelId)
                && string.IsNullOrWhiteSpace(engineOverride)
                && string.IsNullOrWhiteSpace(modelOverride))
            {
                ReplaceRecording(current, current with { IsTranscribing = false });
                SetLocalizedStatus("Recorder.SavedNoModel");
                return null;
            }

            var samples = await loadSamples(ct);
            if (samples.Length == 0)
                throw new InvalidOperationException(Loc.Instance["Recorder.NoAudioCaptured"]);

            await using var modelScope = await _modelManager.BeginTranscriptionRequestAsync(
                engineOverride,
                modelOverride,
                false,
                ct);

            var configuredLanguageHints = settings.GetLanguageHints();
            var configuredLanguage = configuredLanguageHints.FirstOrDefault();
            var task = CurrentRecorderTask;
            var translationTarget = NormalizeOptional(TranslationTargetLanguage);
            var activeResult = await _modelManager.TranscribeActiveWithLanguageHintsAsync(
                samples,
                configuredLanguageHints,
                task,
                prompt: null,
                ct);
            var transcript = await PostProcessTranscriptAsync(
                activeResult.Result,
                task,
                configuredLanguage,
                configuredLanguageHints,
                translationTarget,
                ct);
            if (string.IsNullOrWhiteSpace(transcript) && !string.IsNullOrWhiteSpace(liveTranscript))
                transcript = liveTranscript;

            if (!string.IsNullOrEmpty(transcript))
            {
                var txtPath = Path.ChangeExtension(current.FilePath, ".txt");
                await File.WriteAllTextAsync(txtPath, transcript, ct);
            }

            ReplaceRecording(current, current with
            {
                Transcript = transcript,
                IsTranscribing = false,
                ErrorMessage = null
            });
            SetLocalizedStatus("Status.Done");
            return transcript;
        }
        catch (OperationCanceledException)
        {
            ReplaceRecording(current, current with
            {
                IsTranscribing = false,
                ErrorMessage = Loc.Instance["Status.Cancelled"]
            });
            throw;
        }
        catch (IOException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (InvalidOperationException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (ModelManagerRequestException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (NotSupportedException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        finally
        {
            _modelManager.ScheduleAutoUnload();
            DispatchToUi(RefreshRecordingCommandState);
        }

        return null;
    }

    private void HandleTranscriptionFailure(RecordingItem current, Exception ex)
    {
        var message = ex.Message;
        _errorLog.AddEntry(message, ErrorCategory.Transcription);
        ReplaceRecording(current, current with
        {
            IsTranscribing = false,
            ErrorMessage = Loc.Instance.GetString("Recorder.FailedFormat", message)
        });
        SetErrorStatus(message);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteRecording))]
    private void DeleteRecording(RecordingItem? item)
    {
        if (item is null || item.IsTranscribing)
            return;

        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
            var txt = Path.ChangeExtension(item.FilePath, ".txt");
            if (File.Exists(txt)) File.Delete(txt);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Deleting recorder files failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Deleting recorder files failed: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Deleting recorder files failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Deleting recorder files failed: {ex.Message}");
        }
        Recordings.Remove(item);
        OnPropertyChanged(nameof(HasRecordings));
        RefreshRecordingCommandState();
    }

    private static bool CanDeleteRecording(RecordingItem? item) =>
        item is { IsTranscribing: false };

    [RelayCommand]
    private static void CopyTranscript(RecordingItem? item)
    {
        if (string.IsNullOrWhiteSpace(item?.Transcript))
            return;

        try
        {
            Clipboard.SetText(item.Transcript);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Debug.WriteLine($"Copying recorder transcript failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private static void RevealRecording(RecordingItem? item)
    {
        if (item is null || !File.Exists(item.FilePath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{item.FilePath}\"",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private static void OpenRecordingsFolder()
    {
        Directory.CreateDirectory(TypeWhisperEnvironment.AudioPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = TypeWhisperEnvironment.AudioPath,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Starts recording for the local API.
    /// </summary>
    public async Task<Guid> StartRecordingForApiAsync(
        bool micEnabled,
        bool systemAudioEnabled,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _activeApiSessionId = id;
        StoreApiSession(new RecorderApiSessionSnapshot(
            id,
            RecorderSessionStatus.Recording,
            null,
            null,
            null));

        var started = await StartRecordingCoreAsync(micEnabled, systemAudioEnabled, ct);
        if (!started)
            FailActiveApiSession(StatusText);

        return id;
    }

    /// <summary>
    /// Stops recording for the local API.
    /// </summary>
    public async Task<Guid?> StopRecordingForApiAsync(CancellationToken ct)
    {
        var id = _activeApiSessionId;
        if (id is not null)
            MarkActiveApiSession(RecorderSessionStatus.Finalizing);

        await StopRecordingCoreAsync(ct);
        return id;
    }

    /// <summary>
    /// Returns a recorder API session by id.
    /// </summary>
    public RecorderApiSessionSnapshot? GetRecorderApiSession(Guid id)
    {
        lock (_apiSessionLock)
        {
            return _apiSessions.TryGetValue(id, out var session) ? session : null;
        }
    }

    private void LoadExistingRecordings()
    {
        try
        {
            var dir = TypeWhisperEnvironment.AudioPath;
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory
                .GetFiles(dir, "recording-*.*")
                .Where(AudioFileService.IsSupported)
                .OrderByDescending(f => f))
            {
                var fi = new FileInfo(file);
                var txtFile = Path.ChangeExtension(file, ".txt");
                var transcript = File.Exists(txtFile) ? File.ReadAllText(txtFile) : null;
                var duration = TryGetDuration(file);
                Recordings.Add(new RecordingItem(fi.Name, file, fi.CreationTime, duration, transcript));
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Loading recorder files failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Loading recorder files failed: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Loading recorder files failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Loading recorder files failed: {ex.Message}");
        }
    }

    private static TimeSpan TryGetDuration(string file)
    {
        try
        {
            return AudioFileService.GetDuration(file);
        }
        catch (IOException)
        {
            return TimeSpan.Zero;
        }
        catch (UnauthorizedAccessException)
        {
            return TimeSpan.Zero;
        }
        catch (NotSupportedException)
        {
            return TimeSpan.Zero;
        }
        catch (ArgumentException)
        {
            return TimeSpan.Zero;
        }
    }

    private void LoadRecorderSettings(AppSettings settings)
    {
        _isLoadingSettings = true;
        try
        {
            MicEnabled = settings.RecorderMicEnabled;
            SystemAudioEnabled = settings.RecorderSystemAudioEnabled;
            RefreshSystemAudioDevices(settings.RecorderSystemAudioDeviceId);
            OutputFormat = RecorderSettings.ParseOutputFormat(settings.RecorderOutputFormat);
            TrackMode = RecorderSettings.ParseTrackMode(settings.RecorderTrackMode);
            MicDuckingMode = RecorderSettings.ParseDuckingMode(settings.RecorderMicDuckingMode);
            TranscriptionEnabled = settings.RecorderTranscriptionEnabled;
            TranslationModeEnabled = RecorderTranslationModeFromSettings(settings);
            TranslationTargetLanguage = settings.RecorderTranslationTargetLanguage;
            TranscriptionEngineOverride = settings.RecorderTranscriptionEngineOverride;
            TranscriptionModelOverride = settings.RecorderTranscriptionModelOverride;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveRecorderSettings()
    {
        _settings.Save(_settings.Current with
        {
            RecorderMicEnabled = MicEnabled,
            RecorderSystemAudioEnabled = SystemAudioEnabled,
            RecorderSystemAudioDeviceId = SelectedSystemAudioDevice?.Id,
            RecorderOutputFormat = RecorderSettings.ToSettingsValue(OutputFormat),
            RecorderTrackMode = RecorderSettings.ToSettingsValue(TrackMode),
            RecorderMicDuckingMode = RecorderSettings.ToSettingsValue(MicDuckingMode),
            RecorderTranscriptionEnabled = TranscriptionEnabled,
            RecorderTranscriptionTask = TranslationModeEnabled ? "translate" : "transcribe",
            RecorderTranslationTargetLanguage = TranslationModeEnabled ? NormalizeOptional(TranslationTargetLanguage) : null,
            RecorderTranscriptionEngineOverride = NormalizeOptional(TranscriptionEngineOverride),
            RecorderTranscriptionModelOverride = NormalizeOptional(TranscriptionModelOverride)
        });
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (IsRecording)
            return;

        DispatchToUi(() =>
        {
            if (RecorderSettingsMatch(settings))
                return;

            LoadRecorderSettings(settings);
        });
    }

    private bool RecorderSettingsMatch(AppSettings settings) =>
        MicEnabled == settings.RecorderMicEnabled
        && SystemAudioEnabled == settings.RecorderSystemAudioEnabled
        && string.Equals(SelectedSystemAudioDevice?.Id, settings.RecorderSystemAudioDeviceId, StringComparison.OrdinalIgnoreCase)
        && OutputFormat == RecorderSettings.ParseOutputFormat(settings.RecorderOutputFormat)
        && TrackMode == RecorderSettings.ParseTrackMode(settings.RecorderTrackMode)
        && MicDuckingMode == RecorderSettings.ParseDuckingMode(settings.RecorderMicDuckingMode)
        && TranscriptionEnabled == settings.RecorderTranscriptionEnabled
        && TranslationModeEnabled == RecorderTranslationModeFromSettings(settings)
        && string.Equals(NormalizeOptional(TranslationTargetLanguage), NormalizeOptional(settings.RecorderTranslationTargetLanguage), StringComparison.Ordinal)
        && string.Equals(NormalizeOptional(TranscriptionEngineOverride), NormalizeOptional(settings.RecorderTranscriptionEngineOverride), StringComparison.Ordinal)
        && string.Equals(NormalizeOptional(TranscriptionModelOverride), NormalizeOptional(settings.RecorderTranscriptionModelOverride), StringComparison.Ordinal);

    private static bool IsRecorderSettingProperty(string? propertyName) =>
        propertyName is nameof(MicEnabled)
            or nameof(SystemAudioEnabled)
            or nameof(SelectedSystemAudioDevice)
            or nameof(OutputFormat)
            or nameof(TrackMode)
            or nameof(MicDuckingMode)
            or nameof(TranscriptionEnabled)
            or nameof(TranslationModeEnabled)
            or nameof(TranslationTargetLanguage)
            or nameof(TranscriptionEngineOverride)
            or nameof(TranscriptionModelOverride);

    private void RefreshSystemAudioDevices(string? selectedDeviceId)
    {
        SystemAudioDevices.Clear();
        var defaultDevice = new SystemAudioOutputDevice(null, Loc.Instance["Recorder.SystemAudioOutputDefault"]);
        SystemAudioDevices.Add(defaultDevice);

        IReadOnlyList<SystemAudioOutputDevice> devices = [];
        try
        {
            devices = _capture.GetSystemAudioOutputDevices();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"Failed to enumerate system-audio output devices: {ex.Message}");
        }

        foreach (var device in devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase))
            SystemAudioDevices.Add(device);

        var selected = SystemAudioDevices.FirstOrDefault(device =>
            string.Equals(device.Id, selectedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !string.IsNullOrWhiteSpace(selectedDeviceId))
        {
            selected = new SystemAudioOutputDevice(
                selectedDeviceId,
                Loc.Instance.GetString("Recorder.SystemAudioOutputMissingFormat", selectedDeviceId));
            SystemAudioDevices.Add(selected);
        }

        SelectedSystemAudioDevice = selected ?? defaultDevice;
    }

    private void StoreApiSession(RecorderApiSessionSnapshot session)
    {
        lock (_apiSessionLock)
        {
            _apiSessions[session.Id] = session;
        }
    }

    private void MarkActiveApiSession(RecorderSessionStatus status)
    {
        var id = _activeApiSessionId;
        if (id is null)
            return;

        lock (_apiSessionLock)
        {
            if (_apiSessions.TryGetValue(id.Value, out var session))
                _apiSessions[id.Value] = session with { Status = status };
        }
    }

    private void CompleteActiveApiSession(string? text, string outputFile)
    {
        var id = _activeApiSessionId;
        if (id is null)
            return;

        StoreApiSession(new RecorderApiSessionSnapshot(
            id.Value,
            RecorderSessionStatus.Completed,
            text,
            outputFile,
            null));
        _activeApiSessionId = null;
    }

    private void FailActiveApiSession(string error)
    {
        var id = _activeApiSessionId;
        if (id is null)
            return;

        StoreApiSession(new RecorderApiSessionSnapshot(
            id.Value,
            RecorderSessionStatus.Failed,
            null,
            null,
            error));
        _activeApiSessionId = null;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private TranscriptionTask CurrentRecorderTask =>
        TranslationModeEnabled ? TranscriptionTask.Translate : TranscriptionTask.Transcribe;

    private static bool RecorderTranslationModeFromSettings(AppSettings settings) =>
        string.Equals(settings.RecorderTranscriptionTask, "translate", StringComparison.OrdinalIgnoreCase);

    private string DefaultRecorderTranslationTargetLanguage() =>
        NormalizeOptional(_settings.Current.RecorderTranslationTargetLanguage)
        ?? NormalizeOptional(_settings.Current.LastTranslationTargetLanguage)
        ?? NormalizeOptional(_settings.Current.TranslationTargetLanguage)
        ?? (TranslationModelInfo.SupportedLanguages.Any(language => language.Code == "en")
            ? "en"
            : TranslationModelInfo.SupportedLanguages.FirstOrDefault()?.Code ?? "en");

    private RecordingItem ReplaceRecording(RecordingItem item, RecordingItem replacement)
    {
        DispatchToUi(() =>
        {
            var index = Recordings.IndexOf(item);
            if (index < 0)
            {
                for (var i = 0; i < Recordings.Count; i++)
                {
                    if (string.Equals(Recordings[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index >= 0)
                Recordings[index] = replacement;

            RefreshRecordingCommandState();
        });

        return replacement;
    }

    private void RefreshRecordingCommandState()
    {
        IsTranscribing = Recordings.Any(item => item.IsTranscribing);
        TranscribeRecordingCommand.NotifyCanExecuteChanged();
        DeleteRecordingCommand.NotifyCanExecuteChanged();
    }

    private void SetLocalizedStatus(string key)
    {
        _statusKey = key;
        StatusText = Loc.Instance[key];
    }

    private void SetErrorStatus(string message)
    {
        _statusKey = null;
        StatusText = Loc.Instance.GetString("Status.ErrorFormat", message);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_statusKey))
            return;

        DispatchToUi(() =>
        {
            StatusText = Loc.Instance[_statusKey];
        });
    }

    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
        _streamingHandler.Dispose();
        _timer?.Dispose();
        _capture.Dispose();
        _disposed = true;
    }
}

internal sealed class NoopDictionaryService : IDictionaryService
{
    public static NoopDictionaryService Instance { get; } = new();

    private NoopDictionaryService()
    {
    }

    public IReadOnlyList<DictionaryEntry> Entries => [];
    public event Action? EntriesChanged
    {
        add { }
        remove { }
    }

    public void AddEntry(DictionaryEntry entry) { }
    public void AddEntries(IEnumerable<DictionaryEntry> entries) { }
    public void UpdateEntry(DictionaryEntry entry) { }
    public void DeleteEntry(string id) { }
    public void DeleteEntries(IEnumerable<string> ids) { }
    public string ApplyCorrections(string text) => text;
    public string? GetTermsForPrompt() => null;
    public void LearnCorrection(string original, string replacement) { }
    public IReadOnlyList<LearnedDictionaryCorrection> LearnCorrections(IEnumerable<CorrectionSuggestion> suggestions) => [];
    public void UndoLearnedCorrections(IEnumerable<LearnedDictionaryCorrection> learnedCorrections) { }
    public void ActivatePack(TermPack pack) { }
    public void DeactivatePack(string packId) { }
}
