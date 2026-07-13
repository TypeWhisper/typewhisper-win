using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Guides microphone samples into reviewed dictionary corrections.
/// </summary>
public sealed partial class DictionaryTrainingViewModel : ObservableObject
{
    private readonly IDictionaryService _dictionary;
    private readonly ISettingsService _settings;
    private readonly ModelManagerService _modelManager;
    private readonly AudioRecordingService _audio;
    private readonly HotkeyService _hotkeys;
    private CancellationTokenSource? _sessionCts;
    private long _sessionId;
    private DictionaryTrainingSampleViewModel? _recordingSample;
    private string? _modelId;
    private bool _restoreHotkeys;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isSetup = true;
    [ObservableProperty] private bool _isSampling;
    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _targetWord = "";
    [ObservableProperty] private string _engineDisplayName = "";
    [ObservableProperty] private string _modelDisplayName = "";
    [ObservableProperty] private string _errorText = "";

    /// <summary>
    /// Gets the editable training samples.
    /// </summary>
    public ObservableCollection<DictionaryTrainingSampleViewModel> Samples { get; } = [];

    /// <summary>
    /// Gets the reviewed correction candidates.
    /// </summary>
    public ObservableCollection<DictionaryTrainingCandidateViewModel> Candidates { get; } = [];

    /// <summary>
    /// Gets whether no correction candidate was found.
    /// </summary>
    public bool HasNoCandidates => Candidates.Count == 0;

    /// <summary>
    /// Gets whether an error message is visible.
    /// </summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>
    /// Raised after reviewed entries are saved.
    /// </summary>
    public event Action<string>? Completed;

    /// <summary>
    /// Initializes a new instance of the DictionaryTrainingViewModel class.
    /// </summary>
    public DictionaryTrainingViewModel(
        IDictionaryService dictionary,
        ISettingsService settings,
        ModelManagerService modelManager,
        AudioRecordingService audio,
        HotkeyService hotkeys)
    {
        _dictionary = dictionary;
        _settings = settings;
        _modelManager = modelManager;
        _audio = audio;
        _hotkeys = hotkeys;
    }

    partial void OnTargetWordChanged(string value) => BeginCommand.NotifyCanExecuteChanged();

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void Open()
    {
        CancelSession();
        IsBusy = false;
        TargetWord = "";
        ErrorText = "";
        Samples.Clear();
        Candidates.Clear();
        SetStage(setup: true);
        SnapshotSelectedModel();
        IsOpen = true;
        if (_audio.IsRecording)
        {
            ErrorText = Loc.Instance["Dictionary.TrainingAudioBusy"];
            return;
        }

        _sessionCts = new CancellationTokenSource();
        _restoreHotkeys = _hotkeys.IsEnabled;
        _hotkeys.IsEnabled = false;
    }

    [RelayCommand(CanExecute = nameof(CanBegin))]
    private void Begin()
    {
        var word = TargetWord.Trim();
        if (!IsTrainableWord(word))
        {
            ErrorText = Loc.Instance["Dictionary.TrainingInvalidWord"];
            return;
        }

        if (string.IsNullOrWhiteSpace(_modelId))
        {
            ErrorText = Loc.Instance["Dictionary.TrainingNoModel"];
            return;
        }

        TargetWord = word;
        ErrorText = "";
        Samples.Clear();
        for (var index = 1; index <= 3; index++)
        {
            var sample = new DictionaryTrainingSampleViewModel(
                index,
                Loc.Instance.GetString($"Dictionary.TrainingSentence{index}Format", word));
            sample.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DictionaryTrainingSampleViewModel.Status))
                    ReviewCommand.NotifyCanExecuteChanged();
            };
            Samples.Add(sample);
        }

        SetStage(sampling: true);
        ReviewCommand.NotifyCanExecuteChanged();
    }

    private bool CanBegin() =>
        _sessionCts is not null && !string.IsNullOrWhiteSpace(TargetWord) && !IsBusy;

    [RelayCommand]
    private async Task ToggleSample(DictionaryTrainingSampleViewModel? sample)
    {
        if (sample is null || IsBusy || !IsOpen)
            return;

        if (_recordingSample is not null)
        {
            if (ReferenceEquals(_recordingSample, sample))
                await StopAndTranscribeAsync(sample);
            return;
        }

        await StartSampleAsync(sample);
    }

    private async Task StartSampleAsync(DictionaryTrainingSampleViewModel sample)
    {
        var session = _sessionCts;
        if (session is null)
            return;

        var sessionId = _sessionId;
        ErrorText = "";
        IsBusy = true;
        try
        {
            var modelLoaded = !string.IsNullOrWhiteSpace(_modelId) &&
                await _modelManager.EnsureModelLoadedAsync(_modelId, session.Token);
            if (!OwnsSession(sessionId, session))
                return;

            if (!modelLoaded)
            {
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingNoModel"]);
                return;
            }

            if (!_audio.HasDevice)
            {
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingNoMicrophone"]);
                return;
            }

            if (_audio.IsRecording)
            {
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingAudioBusy"]);
                return;
            }

            _audio.StartRecording();
            if (!_audio.IsRecording)
            {
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingNoMicrophone"]);
                return;
            }

            _recordingSample = sample;
            sample.Status = DictionaryTrainingSampleStatus.Recording;
            sample.StatusText = Loc.Instance["Dictionary.TrainingRecording"];
        }
        catch (OperationCanceledException)
        {
            if (OwnsSession(sessionId, session))
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingCancelled"]);
        }
        catch (Exception ex)
        {
            if (OwnsSession(sessionId, session))
                SetSampleError(sample, Loc.Instance.GetString("Dictionary.TrainingFailureFormat", ex.Message));
        }
        finally
        {
            if (OwnsSession(sessionId, session))
                IsBusy = false;
        }
    }

    private async Task StopAndTranscribeAsync(DictionaryTrainingSampleViewModel sample)
    {
        var session = _sessionCts;
        if (session is null)
            return;

        var sessionId = _sessionId;
        IsBusy = true;
        sample.Status = DictionaryTrainingSampleStatus.Processing;
        sample.StatusText = Loc.Instance["Dictionary.TrainingTranscribing"];
        _recordingSample = null;

        try
        {
            var samples = await _audio.StopRecordingAsync(session.Token);
            if (!OwnsSession(sessionId, session))
                return;

            if (samples is not { Length: > 0 })
            {
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingNoSpeech"]);
                return;
            }

            var duration = samples.Length / 16000.0;
            var transcriptionSamples = DictationShortSpeechPolicy.PadSamplesForFinalTranscription(samples, duration);
            var result = await _modelManager.Engine.TranscribeWithLanguageHintsAsync(
                transcriptionSamples,
                _settings.Current.GetLanguageHints(),
                TranscriptionTask.Transcribe,
                session.Token);
            if (!OwnsSession(sessionId, session))
                return;

            var rawText = DictationFinalTextPolicy.SelectRawText(result.Text);
            if (string.IsNullOrWhiteSpace(rawText))
            {
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingNoSpeech"]);
                return;
            }

            sample.RawTranscript = rawText;
            sample.Status = DictionaryTrainingSampleStatus.Completed;
            sample.StatusText = Loc.Instance["Dictionary.TrainingSampleComplete"];
        }
        catch (OperationCanceledException)
        {
            if (OwnsSession(sessionId, session))
                SetSampleError(sample, Loc.Instance["Dictionary.TrainingCancelled"]);
        }
        catch (Exception ex)
        {
            if (OwnsSession(sessionId, session))
                SetSampleError(sample, Loc.Instance.GetString("Dictionary.TrainingFailureFormat", ex.Message));
        }
        finally
        {
            if (OwnsSession(sessionId, session))
            {
                if (_audio.IsRecording)
                    _audio.StopRecording();
                IsBusy = false;
                ReviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool OwnsSession(long sessionId, CancellationTokenSource session) =>
        IsOpen && _sessionId == sessionId && ReferenceEquals(_sessionCts, session) && !session.IsCancellationRequested;

    private static void SetSampleError(DictionaryTrainingSampleViewModel sample, string message)
    {
        sample.Status = DictionaryTrainingSampleStatus.Error;
        sample.StatusText = message;
    }

    [RelayCommand(CanExecute = nameof(CanReview))]
    private void Review()
    {
        RebuildCandidates();
        ErrorText = "";
        SetStage(reviewing: true);
    }

    private bool CanReview() =>
        Samples.Count >= 3 && Samples.All(sample => sample.Status == DictionaryTrainingSampleStatus.Completed);

    [RelayCommand]
    private void BackToSamples()
    {
        ErrorText = "";
        SetStage(sampling: true);
    }

    [RelayCommand]
    private void Save()
    {
        ValidateCandidates();
        var entries = new List<DictionaryEntry>();
        if (!_dictionary.Entries.Any(entry =>
                entry.EntryType == DictionaryEntryType.Term &&
                entry.Original.Equals(TargetWord, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Term,
                Original = TargetWord,
                Source = DictionaryEntrySource.Manual
            });
        }

        entries.AddRange(Candidates
            .Where(candidate => candidate.IsApproved && candidate.CanApprove)
            .Select(candidate => new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = candidate.Original.Trim(),
                Replacement = TargetWord,
                Source = DictionaryEntrySource.Manual
            }));

        if (entries.Count > 0)
            _dictionary.AddEntries(entries);

        var word = TargetWord;
        CloseSession();
        Completed?.Invoke(word);
    }

    [RelayCommand]
    private void Cancel() => CloseSession();

    private void CloseSession()
    {
        CancelSession();
        IsOpen = false;
        IsBusy = false;
        _recordingSample = null;
    }

    private void CancelSession()
    {
        _sessionId++;
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        if (_recordingSample is not null && _audio.IsRecording)
            _audio.StopRecording();
        _recordingSample = null;
        if (_restoreHotkeys)
            _hotkeys.IsEnabled = true;
        _restoreHotkeys = false;
    }

    private void RebuildCandidates()
    {
        Candidates.Clear();
        var originals = Samples
            .Select(sample => ExtractCandidate(TargetWord, sample.Sentence, sample.RawTranscript))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var original in originals)
            Candidates.Add(new DictionaryTrainingCandidateViewModel(original, ValidateCandidates));

        ValidateCandidates();
        OnPropertyChanged(nameof(HasNoCandidates));
    }

    private void ValidateCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = _dictionary.Entries.Where(entry => entry.EntryType == DictionaryEntryType.Correction).ToArray();
        foreach (var candidate in Candidates)
        {
            var original = candidate.Original.Trim();
            if (!IsTrainableWord(original) ||
                original.Equals(TargetWord, StringComparison.OrdinalIgnoreCase))
            {
                candidate.SetValidation(Loc.Instance["Dictionary.TrainingInvalidCandidate"], canApprove: false);
                continue;
            }

            if (!seen.Add(original))
            {
                candidate.SetValidation(Loc.Instance["Dictionary.TrainingDuplicateCandidate"], canApprove: false);
                continue;
            }

            var existingEntry = existing.FirstOrDefault(entry =>
                entry.Original.Equals(original, StringComparison.OrdinalIgnoreCase));
            if (existingEntry is null)
            {
                candidate.SetValidation("", canApprove: true);
                continue;
            }

            var sameReplacement = string.Equals(existingEntry.Replacement, TargetWord, StringComparison.Ordinal);
            candidate.SetValidation(
                sameReplacement
                    ? Loc.Instance["Dictionary.TrainingAlreadyExists"]
                    : Loc.Instance.GetString("Dictionary.TrainingConflictFormat", existingEntry.Replacement ?? ""),
                canApprove: false);
        }
    }

    internal static string? ExtractCandidate(string targetWord, string expectedSentence, string rawTranscript)
    {
        var suggestions = TextDiffService.ExtractHighConfidenceCorrections(
            NormalizeSentenceForDiff(rawTranscript),
            NormalizeSentenceForDiff(expectedSentence),
            int.MaxValue);
        if (suggestions.Count != 1 ||
            !suggestions[0].Replacement.Equals(targetWord, StringComparison.Ordinal))
        {
            return null;
        }

        return suggestions[0].Original;
    }

    private static string NormalizeSentenceForDiff(string value) =>
        string.Join(" ", value
            .Split(null as char[], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('.', ',', '!', '?', ':', ';', '。', '"', '(', ')', '[', ']', '{', '}'))
            .Where(token => token.Length > 0));

    internal static bool IsTrainableWord(string value)
    {
        var word = value.Trim();
        return word.Length > 0 &&
            char.IsLetterOrDigit(word[0]) &&
            char.IsLetterOrDigit(word[^1]) &&
            word.All(character => char.IsLetterOrDigit(character) || character is '-' or '\'');
    }

    private void SnapshotSelectedModel()
    {
        _modelId = _settings.Current.SelectedModelId;
        EngineDisplayName = Loc.Instance["Dictionary.TrainingNoModelValue"];
        ModelDisplayName = Loc.Instance["Dictionary.TrainingNoModelValue"];
        if (string.IsNullOrWhiteSpace(_modelId) || !ModelManagerService.IsPluginModel(_modelId))
            return;

        var (pluginId, modelId) = ModelManagerService.ParsePluginModelId(_modelId);
        var engine = _modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
            candidate.GetTranscriptionSelectionId().Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (engine is null)
            return;

        EngineDisplayName = engine.ProviderDisplayName;
        ModelDisplayName = engine.TranscriptionModels
            .FirstOrDefault(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? modelId;
    }

    private void SetStage(bool setup = false, bool sampling = false, bool reviewing = false)
    {
        IsSetup = setup;
        IsSampling = sampling;
        IsReviewing = reviewing;
    }
}

/// <summary>
/// Lists the states of one microphone training sample.
/// </summary>
public enum DictionaryTrainingSampleStatus
{
    /// <summary>Ready to record.</summary>
    Ready,
    /// <summary>Currently recording.</summary>
    Recording,
    /// <summary>Currently transcribing.</summary>
    Processing,
    /// <summary>Successfully transcribed.</summary>
    Completed,
    /// <summary>Failed and may be retried.</summary>
    Error
}

/// <summary>
/// Represents one editable training sentence and its raw transcript.
/// </summary>
public sealed partial class DictionaryTrainingSampleViewModel : ObservableObject
{
    /// <summary>
    /// Gets the one-based sample number.
    /// </summary>
    public int Number { get; }

    private string _sentence;
    [ObservableProperty] private string _rawTranscript = "";
    [ObservableProperty] private DictionaryTrainingSampleStatus _status;
    [ObservableProperty] private string _statusText = "";

    /// <summary>
    /// Gets whether this sample is currently recording.
    /// </summary>
    public bool IsRecording => Status == DictionaryTrainingSampleStatus.Recording;

    /// <summary>
    /// Gets whether a raw transcript is available.
    /// </summary>
    public bool HasRawTranscript => !string.IsNullOrWhiteSpace(RawTranscript);

    /// <summary>
    /// Gets or sets the editable sentence. Changes invalidate an earlier recording.
    /// </summary>
    public string Sentence
    {
        get => _sentence;
        set
        {
            if (IsSentenceLocked || !SetProperty(ref _sentence, value))
                return;

            RawTranscript = "";
            Status = DictionaryTrainingSampleStatus.Ready;
            StatusText = Loc.Instance["Dictionary.TrainingReady"];
        }
    }

    /// <summary>
    /// Gets whether the sentence must remain unchanged during capture or transcription.
    /// </summary>
    public bool IsSentenceLocked => Status is
        DictionaryTrainingSampleStatus.Recording or DictionaryTrainingSampleStatus.Processing;

    /// <summary>
    /// Gets the localized recording action.
    /// </summary>
    public string ActionText => IsRecording
        ? Loc.Instance["Dictionary.TrainingStopRecording"]
        : Loc.Instance["Dictionary.TrainingRecord"];

    /// <summary>
    /// Initializes a new sample.
    /// </summary>
    public DictionaryTrainingSampleViewModel(int number, string sentence)
    {
        Number = number;
        _sentence = sentence;
        _statusText = Loc.Instance["Dictionary.TrainingReady"];
    }

    partial void OnStatusChanged(DictionaryTrainingSampleStatus value)
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsSentenceLocked));
    }

    partial void OnRawTranscriptChanged(string value) => OnPropertyChanged(nameof(HasRawTranscript));
}

/// <summary>
/// Represents one editable correction candidate.
/// </summary>
public sealed partial class DictionaryTrainingCandidateViewModel : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty] private string _original;
    [ObservableProperty] private bool _isApproved = true;
    [ObservableProperty] private bool _canApprove = true;
    [ObservableProperty] private string _message = "";

    /// <summary>
    /// Gets whether validation feedback is available.
    /// </summary>
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>
    /// Initializes a new candidate.
    /// </summary>
    public DictionaryTrainingCandidateViewModel(string original, Action changed)
    {
        _original = original;
        _changed = changed;
    }

    partial void OnOriginalChanged(string value) => _changed();

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    internal void SetValidation(string message, bool canApprove)
    {
        var wasApprovable = CanApprove;
        CanApprove = canApprove;
        Message = message;
        if (!canApprove)
            IsApproved = false;
        else if (!wasApprovable)
            IsApproved = true;
    }
}
