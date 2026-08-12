using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.ViewModels;

internal interface IRecordingOverlayDictationSource : INotifyPropertyChanged
{
    bool IsOverlayVisible { get; }
    bool ShowFeedback { get; }
    bool ShowInlineFeedback { get; }
    bool HasOverlayContentVisible { get; }
    bool ShowDetachedFeedback { get; }
    DictationState State { get; }
    float AudioLevel { get; }
    double RecordingSeconds { get; }
    string? CancelWarningText { get; }
    string StatusText { get; }
    HotkeyMode? CurrentHotkeyMode { get; }
    string? ActiveProcessName { get; }
    string? ActiveWorkflowName { get; }
    string? FeedbackText { get; }
    bool FeedbackIsError { get; }
    string? FeedbackActionText { get; }
    ICommand? FeedbackActionCommand { get; }
    bool ShowFeedbackAction { get; }
}

internal interface IRecordingOverlayRecorderSource : INotifyPropertyChanged
{
    RecorderActivityState ActivityState { get; }
    float AudioLevel { get; }
    double RecordingSeconds { get; }
    string StatusText { get; }
}

/// <summary>
/// Presents the active recording source to the overlay, preferring dictation over recorder capture.
/// </summary>
public sealed class RecordingOverlayViewModel : ObservableObject
{
    private readonly IRecordingOverlayDictationSource _dictation;
    private readonly IRecordingOverlayRecorderSource _recorder;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, object?> _lastPublishedValues = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the RecordingOverlayViewModel class.
    /// </summary>
    public RecordingOverlayViewModel(
        DictationViewModel dictation,
        AudioRecorderViewModel recorder,
        ISettingsService settings)
        : this(
            new DictationSourceAdapter(dictation),
            new RecorderSourceAdapter(recorder),
            settings)
    {
    }

    internal RecordingOverlayViewModel(
        IRecordingOverlayDictationSource dictation,
        IRecordingOverlayRecorderSource recorder,
        ISettingsService settings)
    {
        _dictation = dictation;
        _recorder = recorder;
        _settings = settings;

        _dictation.PropertyChanged += OnSourcePropertyChanged;
        _recorder.PropertyChanged += OnSourcePropertyChanged;
        _settings.SettingsChanged += _ => RefreshAll();
        CaptureCurrentValues();
    }

    /// <summary>
    /// Gets the left overlay widget.
    /// </summary>
    public OverlayWidget LeftWidget => _settings.Current.OverlayLeftWidget;
    /// <summary>
    /// Gets the right overlay widget.
    /// </summary>
    public OverlayWidget RightWidget => _settings.Current.OverlayRightWidget;
    /// <summary>
    /// Gets the indicator style.
    /// </summary>
    public IndicatorStyle IndicatorStyle => _settings.Current.IndicatorStyle;
    /// <summary>
    /// Gets the overlay position.
    /// </summary>
    public OverlayPosition OverlayPosition => _settings.Current.OverlayPosition;
    /// <summary>
    /// Gets whether inline feedback is visible.
    /// </summary>
    public bool ShowInlineFeedback => UseDictation && _dictation.ShowInlineFeedback;
    /// <summary>
    /// Gets whether detached feedback is visible.
    /// </summary>
    public bool ShowDetachedFeedback => UseDictation && _dictation.ShowDetachedFeedback;
    /// <summary>
    /// Gets whether overlay chrome has visible content.
    /// </summary>
    public bool HasOverlayContentVisible => UseDictation
        ? _dictation.HasOverlayContentVisible
        : UseRecorder;
    /// <summary>
    /// Gets whether the main overlay is visible.
    /// </summary>
    public bool IsOverlayVisible => UseDictation
        ? _dictation.IsOverlayVisible
        : UseRecorder;
    /// <summary>
    /// Gets the active overlay state.
    /// </summary>
    public DictationState State => UseDictation
        ? _dictation.State
        : UseRecorder ? DictationState.Recording : DictationState.Idle;
    /// <summary>
    /// Gets the overlay audio level.
    /// </summary>
    public float AudioLevel => UseDictation ? _dictation.AudioLevel : _recorder.AudioLevel;
    /// <summary>
    /// Gets the recording seconds.
    /// </summary>
    public double RecordingSeconds => UseDictation ? _dictation.RecordingSeconds : _recorder.RecordingSeconds;
    /// <summary>
    /// Gets the status text.
    /// </summary>
    public string StatusText => UseDictation
        ? _dictation.CancelWarningText ?? _dictation.StatusText
        : _recorder.StatusText;
    /// <summary>
    /// Gets the current hotkey mode.
    /// </summary>
    public HotkeyMode? CurrentHotkeyMode => UseDictation ? _dictation.CurrentHotkeyMode : null;
    /// <summary>
    /// Gets the active process name.
    /// </summary>
    public string? ActiveProcessName => UseDictation ? _dictation.ActiveProcessName : null;
    /// <summary>
    /// Gets the active workflow name.
    /// </summary>
    public string? ActiveWorkflowName => UseDictation ? _dictation.ActiveWorkflowName : null;
    /// <summary>
    /// Gets feedback text.
    /// </summary>
    public string? FeedbackText => UseDictation ? _dictation.FeedbackText : null;
    /// <summary>
    /// Gets whether feedback is an error.
    /// </summary>
    public bool FeedbackIsError => UseDictation && _dictation.FeedbackIsError;
    /// <summary>
    /// Gets optional feedback action text.
    /// </summary>
    public string? FeedbackActionText => UseDictation ? _dictation.FeedbackActionText : null;
    /// <summary>
    /// Gets optional feedback action command.
    /// </summary>
    public ICommand? FeedbackActionCommand => UseDictation ? _dictation.FeedbackActionCommand : null;
    /// <summary>
    /// Gets whether optional feedback action is visible.
    /// </summary>
    public bool ShowFeedbackAction => UseDictation && _dictation.ShowFeedbackAction;

    private bool UseDictation =>
        _dictation.IsOverlayVisible
        || _dictation.ShowFeedback
        || _dictation.State != DictationState.Idle;

    private bool UseRecorder => _recorder.ActivityState == RecorderActivityState.Recording;

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshAll();

    private void RefreshAll()
    {
        PublishIfChanged(nameof(LeftWidget), LeftWidget);
        PublishIfChanged(nameof(RightWidget), RightWidget);
        PublishIfChanged(nameof(IndicatorStyle), IndicatorStyle);
        PublishIfChanged(nameof(OverlayPosition), OverlayPosition);
        PublishIfChanged(nameof(ShowInlineFeedback), ShowInlineFeedback);
        PublishIfChanged(nameof(HasOverlayContentVisible), HasOverlayContentVisible);
        PublishIfChanged(nameof(ShowDetachedFeedback), ShowDetachedFeedback);
        PublishIfChanged(nameof(IsOverlayVisible), IsOverlayVisible);
        PublishIfChanged(nameof(AudioLevel), AudioLevel);
        PublishIfChanged(nameof(RecordingSeconds), RecordingSeconds);
        PublishIfChanged(nameof(StatusText), StatusText);
        PublishIfChanged(nameof(State), State);
        PublishIfChanged(nameof(CurrentHotkeyMode), CurrentHotkeyMode);
        PublishIfChanged(nameof(ActiveProcessName), ActiveProcessName);
        PublishIfChanged(nameof(ActiveWorkflowName), ActiveWorkflowName);
        PublishIfChanged(nameof(FeedbackText), FeedbackText);
        PublishIfChanged(nameof(FeedbackIsError), FeedbackIsError);
        PublishIfChanged(nameof(FeedbackActionText), FeedbackActionText);
        PublishIfChanged(nameof(FeedbackActionCommand), FeedbackActionCommand);
        PublishIfChanged(nameof(ShowFeedbackAction), ShowFeedbackAction);
    }

    private void CaptureCurrentValues()
    {
        TrackCurrentValue(nameof(LeftWidget), LeftWidget);
        TrackCurrentValue(nameof(RightWidget), RightWidget);
        TrackCurrentValue(nameof(IndicatorStyle), IndicatorStyle);
        TrackCurrentValue(nameof(OverlayPosition), OverlayPosition);
        TrackCurrentValue(nameof(ShowInlineFeedback), ShowInlineFeedback);
        TrackCurrentValue(nameof(HasOverlayContentVisible), HasOverlayContentVisible);
        TrackCurrentValue(nameof(ShowDetachedFeedback), ShowDetachedFeedback);
        TrackCurrentValue(nameof(IsOverlayVisible), IsOverlayVisible);
        TrackCurrentValue(nameof(AudioLevel), AudioLevel);
        TrackCurrentValue(nameof(RecordingSeconds), RecordingSeconds);
        TrackCurrentValue(nameof(StatusText), StatusText);
        TrackCurrentValue(nameof(State), State);
        TrackCurrentValue(nameof(CurrentHotkeyMode), CurrentHotkeyMode);
        TrackCurrentValue(nameof(ActiveProcessName), ActiveProcessName);
        TrackCurrentValue(nameof(ActiveWorkflowName), ActiveWorkflowName);
        TrackCurrentValue(nameof(FeedbackText), FeedbackText);
        TrackCurrentValue(nameof(FeedbackIsError), FeedbackIsError);
        TrackCurrentValue(nameof(FeedbackActionText), FeedbackActionText);
        TrackCurrentValue(nameof(FeedbackActionCommand), FeedbackActionCommand);
        TrackCurrentValue(nameof(ShowFeedbackAction), ShowFeedbackAction);
    }

    private void TrackCurrentValue<T>(string propertyName, T value) =>
        _lastPublishedValues[propertyName] = value;

    private void PublishIfChanged<T>(string propertyName, T value)
    {
        if (_lastPublishedValues.TryGetValue(propertyName, out var previous)
            && Equals(previous, value))
            return;

        _lastPublishedValues[propertyName] = value;
        OnPropertyChanged(propertyName);
    }

    private sealed class DictationSourceAdapter : IRecordingOverlayDictationSource
    {
        private readonly DictationViewModel _source;

        public DictationSourceAdapter(DictationViewModel source) => _source = source;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _source.PropertyChanged += value;
            remove => _source.PropertyChanged -= value;
        }

        public bool IsOverlayVisible => _source.IsOverlayVisible;
        public bool ShowFeedback => _source.ShowFeedback;
        public bool ShowInlineFeedback => _source.ShowInlineFeedback;
        public bool HasOverlayContentVisible => _source.HasOverlayContentVisible;
        public bool ShowDetachedFeedback => _source.ShowDetachedFeedback;
        public DictationState State => _source.State;
        public float AudioLevel => _source.AudioLevel;
        public double RecordingSeconds => _source.RecordingSeconds;
        public string? CancelWarningText => _source.CancelWarningText;
        public string StatusText => _source.StatusText;
        public HotkeyMode? CurrentHotkeyMode => _source.CurrentHotkeyMode;
        public string? ActiveProcessName => _source.ActiveProcessName;
        public string? ActiveWorkflowName => _source.ActiveWorkflowName;
        public string? FeedbackText => _source.FeedbackText;
        public bool FeedbackIsError => _source.FeedbackIsError;
        public string? FeedbackActionText => _source.FeedbackActionText;
        public ICommand? FeedbackActionCommand => _source.FeedbackActionCommand;
        public bool ShowFeedbackAction => _source.ShowFeedbackAction;
    }

    private sealed class RecorderSourceAdapter : IRecordingOverlayRecorderSource
    {
        private readonly AudioRecorderViewModel _source;

        public RecorderSourceAdapter(AudioRecorderViewModel source) => _source = source;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _source.PropertyChanged += value;
            remove => _source.PropertyChanged -= value;
        }

        public RecorderActivityState ActivityState => _source.ActivityState;
        public float AudioLevel => _source.AudioLevel;
        public double RecordingSeconds => _source.RecordingSeconds;
        public string StatusText => _source.StatusText;
    }
}
