using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Presents the active recording source to the overlay, preferring dictation over recorder capture.
/// </summary>
public sealed class RecordingOverlayViewModel : ObservableObject
{
    private readonly DictationViewModel _dictation;
    private readonly AudioRecorderViewModel _recorder;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, object?> _lastPublishedValues = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the RecordingOverlayViewModel class.
    /// </summary>
    public RecordingOverlayViewModel(
        DictationViewModel dictation,
        AudioRecorderViewModel recorder,
        ISettingsService settings)
    {
        _dictation = dictation;
        _recorder = recorder;
        _settings = settings;

        _dictation.PropertyChanged += OnSourcePropertyChanged;
        _recorder.PropertyChanged += OnSourcePropertyChanged;
        _settings.SettingsChanged += _ => RefreshAll();
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
    /// Gets the live transcription font size.
    /// </summary>
    public double LiveTranscriptionFontSize =>
        AppSettings.NormalizeLiveTranscriptionFontSize(_settings.Current.LiveTranscriptionFontSize);
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
        : _recorder.IsRecording;
    /// <summary>
    /// Gets whether the main overlay is visible.
    /// </summary>
    public bool IsOverlayVisible => UseDictation
        ? _dictation.IsOverlayVisible
        : _recorder.IsRecording;
    /// <summary>
    /// Gets the active overlay state.
    /// </summary>
    public DictationState State => UseDictation
        ? _dictation.State
        : _recorder.IsRecording ? DictationState.Recording : DictationState.Idle;
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
    public string StatusText => UseDictation ? _dictation.StatusText : _recorder.StatusText;
    /// <summary>
    /// Gets the partial text.
    /// </summary>
    public string PartialText => UseDictation ? _dictation.PartialText : _recorder.PartialText;
    /// <summary>
    /// Gets whether built-in partial preview is visible.
    /// </summary>
    public bool ShowBuiltInPartialPreview => UseDictation
        ? _dictation.ShowBuiltInPartialPreview
        : DictationOverlayPresentation.ShowBuiltInPartialPreview(
            _recorder.PartialText,
            externalLivePreviewActive: false,
            liveTranscriptionEnabled: _recorder.TranscriptionEnabled,
            IndicatorStyle);
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

    private bool UseDictation =>
        _dictation.IsOverlayVisible
        || _dictation.ShowFeedback
        || _dictation.State != DictationState.Idle;

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshAll();

    private void RefreshAll()
    {
        PublishIfChanged(nameof(LeftWidget), LeftWidget);
        PublishIfChanged(nameof(RightWidget), RightWidget);
        PublishIfChanged(nameof(IndicatorStyle), IndicatorStyle);
        PublishIfChanged(nameof(OverlayPosition), OverlayPosition);
        PublishIfChanged(nameof(LiveTranscriptionFontSize), LiveTranscriptionFontSize);
        PublishIfChanged(nameof(ShowInlineFeedback), ShowInlineFeedback);
        PublishIfChanged(nameof(HasOverlayContentVisible), HasOverlayContentVisible);
        PublishIfChanged(nameof(ShowDetachedFeedback), ShowDetachedFeedback);
        PublishIfChanged(nameof(IsOverlayVisible), IsOverlayVisible);
        PublishIfChanged(nameof(ShowBuiltInPartialPreview), ShowBuiltInPartialPreview);
        PublishIfChanged(nameof(AudioLevel), AudioLevel);
        PublishIfChanged(nameof(RecordingSeconds), RecordingSeconds);
        PublishIfChanged(nameof(StatusText), StatusText);
        PublishIfChanged(nameof(PartialText), PartialText);
        PublishIfChanged(nameof(State), State);
        PublishIfChanged(nameof(CurrentHotkeyMode), CurrentHotkeyMode);
        PublishIfChanged(nameof(ActiveProcessName), ActiveProcessName);
        PublishIfChanged(nameof(ActiveWorkflowName), ActiveWorkflowName);
        PublishIfChanged(nameof(FeedbackText), FeedbackText);
        PublishIfChanged(nameof(FeedbackIsError), FeedbackIsError);
    }

    private void PublishIfChanged<T>(string propertyName, T value)
    {
        if (_lastPublishedValues.TryGetValue(propertyName, out var previous)
            && Equals(previous, value))
            return;

        _lastPublishedValues[propertyName] = value;
        OnPropertyChanged(propertyName);
    }
}
