using System.ComponentModel;
using System.Windows.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class RecordingOverlayViewModelTests
{
    [Fact]
    public void RecorderRecording_ShowsRecorderPresentation()
    {
        var dictation = new FakeDictationSource();
        var recorder = new FakeRecorderSource
        {
            ActivityState = RecorderActivityState.Recording,
            AudioLevel = 0.42f,
            RecordingSeconds = 12.5,
            StatusText = "Recorder recording"
        };
        var sut = CreateViewModel(dictation, recorder);

        Assert.True(sut.HasOverlayContentVisible);
        Assert.True(sut.IsOverlayVisible);
        Assert.Equal(DictationState.Recording, sut.State);
        Assert.Equal(0.42f, sut.AudioLevel);
        Assert.Equal(12.5, sut.RecordingSeconds);
        Assert.Equal("Recorder recording", sut.StatusText);
    }

    [Fact]
    public void RecorderFinalizing_DoesNotPresentAnActiveRecording()
    {
        var recorder = new FakeRecorderSource
        {
            ActivityState = RecorderActivityState.Finalizing,
            StatusText = "Finalizing"
        };
        var sut = CreateViewModel(new FakeDictationSource(), recorder);

        Assert.False(sut.HasOverlayContentVisible);
        Assert.False(sut.IsOverlayVisible);
        Assert.Equal(DictationState.Idle, sut.State);
    }

    [Theory]
    [InlineData(DictationState.Recording)]
    [InlineData(DictationState.Processing)]
    [InlineData(DictationState.Inserting)]
    [InlineData(DictationState.Error)]
    public void ActiveDictation_WinsOverRecorderRecording(DictationState state)
    {
        var dictation = new FakeDictationSource
        {
            State = state,
            IsOverlayVisible = true,
            HasOverlayContentVisible = true,
            StatusText = "Dictation"
        };
        var recorder = new FakeRecorderSource
        {
            ActivityState = RecorderActivityState.Recording,
            StatusText = "Recorder"
        };
        var sut = CreateViewModel(dictation, recorder);

        Assert.True(sut.IsOverlayVisible);
        Assert.Equal(state, sut.State);
        Assert.Equal("Dictation", sut.StatusText);
    }

    [Fact]
    public void VisibleDictationFeedback_WinsOverRecorderRecording()
    {
        var dictation = new FakeDictationSource
        {
            ShowFeedback = true,
            ShowDetachedFeedback = true,
            HasOverlayContentVisible = true,
            FeedbackText = "Saved",
            StatusText = "Ready"
        };
        var recorder = new FakeRecorderSource
        {
            ActivityState = RecorderActivityState.Recording,
            StatusText = "Recorder"
        };
        var sut = CreateViewModel(dictation, recorder);

        Assert.True(sut.HasOverlayContentVisible);
        Assert.False(sut.IsOverlayVisible);
        Assert.True(sut.ShowDetachedFeedback);
        Assert.Equal("Saved", sut.FeedbackText);
        Assert.Equal("Ready", sut.StatusText);
    }

    [Fact]
    public void DictationReturningToIdle_RestoresRunningRecorderPresentation()
    {
        var dictation = new FakeDictationSource();
        var recorder = new FakeRecorderSource
        {
            ActivityState = RecorderActivityState.Recording,
            StatusText = "Recorder"
        };
        var sut = CreateViewModel(dictation, recorder);

        dictation.SetPresentation(DictationState.Processing, isOverlayVisible: true, "Dictation");
        Assert.Equal(DictationState.Processing, sut.State);
        Assert.Equal("Dictation", sut.StatusText);

        dictation.SetPresentation(DictationState.Idle, isOverlayVisible: false, "Ready");
        Assert.True(sut.IsOverlayVisible);
        Assert.Equal(DictationState.Recording, sut.State);
        Assert.Equal("Recorder", sut.StatusText);
    }

    [Fact]
    public void RecorderFinalizing_PublishesEachChangedPresentationPropertyOnce()
    {
        var recorder = new FakeRecorderSource
        {
            ActivityState = RecorderActivityState.Recording,
            StatusText = "Recorder"
        };
        var sut = CreateViewModel(new FakeDictationSource(), recorder);
        var changed = new List<string>();
        sut.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? "");

        recorder.SetActivityState(RecorderActivityState.Finalizing);

        Assert.Equal(1, changed.Count(name => name == nameof(sut.HasOverlayContentVisible)));
        Assert.Equal(1, changed.Count(name => name == nameof(sut.IsOverlayVisible)));
        Assert.Equal(1, changed.Count(name => name == nameof(sut.State)));
        Assert.Equal(3, changed.Count);
    }

    [Fact]
    public void RecorderAudioLevel_DoesNotRepublishVisibility()
    {
        var recorder = new FakeRecorderSource { ActivityState = RecorderActivityState.Recording };
        var sut = CreateViewModel(new FakeDictationSource(), recorder);
        var changed = new List<string>();
        sut.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? "");

        recorder.SetAudioLevel(0.75f);

        Assert.Equal([nameof(sut.AudioLevel)], changed);
    }

    [Fact]
    public void SettingsChange_PublishesOnlyChangedOverlaySetting()
    {
        var settings = new TestSettingsService(AppSettings.Default);
        var sut = new RecordingOverlayViewModel(
            new FakeDictationSource(),
            new FakeRecorderSource(),
            settings);
        var changed = new List<string>();
        sut.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? "");

        settings.Save(settings.Current with { OverlayPosition = OverlayPosition.Top });

        Assert.Equal([nameof(sut.OverlayPosition)], changed);
    }

    private static RecordingOverlayViewModel CreateViewModel(
        FakeDictationSource dictation,
        FakeRecorderSource recorder) =>
        new(dictation, recorder, new TestSettingsService(AppSettings.Default));

    private sealed class FakeDictationSource : IRecordingOverlayDictationSource
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsOverlayVisible { get; set; }
        public bool ShowFeedback { get; set; }
        public bool ShowInlineFeedback { get; set; }
        public bool HasOverlayContentVisible { get; set; }
        public bool ShowDetachedFeedback { get; set; }
        public DictationState State { get; set; } = DictationState.Idle;
        public float AudioLevel { get; set; }
        public double RecordingSeconds { get; set; }
        public string? CancelWarningText { get; set; }
        public string StatusText { get; set; } = "Ready";
        public HotkeyMode? CurrentHotkeyMode { get; set; }
        public string? ActiveProcessName { get; set; }
        public string? ActiveWorkflowName { get; set; }
        public string? FeedbackText { get; set; }
        public bool FeedbackIsError { get; set; }
        public string? FeedbackActionText { get; set; }
        public ICommand? FeedbackActionCommand { get; set; }
        public bool ShowFeedbackAction { get; set; }

        public void SetPresentation(DictationState state, bool isOverlayVisible, string statusText)
        {
            State = state;
            IsOverlayVisible = isOverlayVisible;
            HasOverlayContentVisible = isOverlayVisible || ShowFeedback;
            StatusText = statusText;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    private sealed class FakeRecorderSource : IRecordingOverlayRecorderSource
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public RecorderActivityState ActivityState { get; set; }
        public float AudioLevel { get; set; }
        public double RecordingSeconds { get; set; }
        public string StatusText { get; set; } = "Ready";

        public void SetActivityState(RecorderActivityState state)
        {
            ActivityState = state;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityState)));
        }

        public void SetAudioLevel(float value)
        {
            AudioLevel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioLevel)));
        }
    }

    private sealed class TestSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }
}
