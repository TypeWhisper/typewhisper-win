using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public sealed class EscapeCancellationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-escape-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "escape-cancel.json");

    [Fact]
    public void DoubleWarnsFirstAndCancelsOnSecondPressInsideWindow()
    {
        var confirmation = new EscapeCancelConfirmation();
        Assert.Equal(EscapeCancelDecision.Warn, confirmation.Press(EscapeCancelTarget.Recording, EscapeCancelBehavior.Double, 0));
        Assert.Equal(EscapeCancelTarget.Recording, confirmation.Armed);
        Assert.Equal(EscapeCancelDecision.Cancel, confirmation.Press(EscapeCancelTarget.Recording, EscapeCancelBehavior.Double, 2999));
        Assert.Null(confirmation.Armed);
    }

    [Fact]
    public void DoubleRearmsAfterWindowExpires()
    {
        var confirmation = new EscapeCancelConfirmation();
        confirmation.Press(EscapeCancelTarget.Recording, EscapeCancelBehavior.Double, 0);
        Assert.Equal(EscapeCancelDecision.Warn, confirmation.Press(EscapeCancelTarget.Recording, EscapeCancelBehavior.Double, 3000));
        Assert.Equal(EscapeCancelDecision.Cancel, confirmation.Press(EscapeCancelTarget.Recording, EscapeCancelBehavior.Double, 3100));
    }

    [Fact]
    public void WarningForRecordingDoesNotConfirmCancellingProcessing()
    {
        var confirmation = new EscapeCancelConfirmation();
        confirmation.Press(EscapeCancelTarget.Recording, EscapeCancelBehavior.Double, 0);
        Assert.True(confirmation.Observe(EscapeCancelTarget.Processing, 100));
        Assert.Null(confirmation.Armed);
        Assert.Equal(EscapeCancelDecision.Warn, confirmation.Press(EscapeCancelTarget.Processing, EscapeCancelBehavior.Double, 200));
    }

    [Fact]
    public void ObserveKeepsWarningForSamePhaseUntilItExpires()
    {
        var confirmation = new EscapeCancelConfirmation();
        confirmation.Press(EscapeCancelTarget.Processing, EscapeCancelBehavior.Double, 0);
        Assert.False(confirmation.Observe(EscapeCancelTarget.Processing, 2999));
        Assert.True(confirmation.Observe(EscapeCancelTarget.Processing, 3000));
        Assert.False(confirmation.Observe(null, 3001));
    }

    [Theory]
    [InlineData(EscapeCancelBehavior.Single)]
    [InlineData(EscapeCancelBehavior.Instant)]
    public void SingleAndInstantCancelOnFirstPress(EscapeCancelBehavior behavior)
    {
        var confirmation = new EscapeCancelConfirmation();
        Assert.Equal(EscapeCancelDecision.Cancel, confirmation.Press(EscapeCancelTarget.Recording, behavior, 0));
        Assert.Null(confirmation.Armed);
    }

    [Fact]
    public void FilterOwnsWholePressIncludingRepeatsAndRelease()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((true, true), filter.Key(true, 0, true, false));
        // Cancellation ends availability, but repeats and the release stay consumed.
        Assert.Equal((true, false), filter.Key(true, 500, false, false));
        Assert.Equal((true, false), filter.Key(true, 530, false, false));
        Assert.Equal((true, false), filter.Key(false, 560, false, false));
        Assert.Equal((false, false), filter.Key(true, 600, false, false));
        Assert.Equal((false, false), filter.Key(false, 650, false, false));
    }

    [Fact]
    public void FilterPassesEscapeThroughWhileUnavailable()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((false, false), filter.Key(true, 0, false, false));
        // A press that began while idle stays with the focused app when recording starts mid-repeat.
        Assert.Equal((false, false), filter.Key(true, 500, true, false));
        Assert.Equal((false, false), filter.Key(false, 520, true, false));
        Assert.Equal((true, true), filter.Key(true, 600, true, false));
    }

    [Fact]
    public void FilterLeavesWindowsEscapeChordsAlone()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((false, false), filter.Key(true, 0, true, true));
        Assert.Equal((false, false), filter.Key(false, 50, true, true));
    }

    [Fact]
    public void FilterTreatsPressAfterLostReleaseAsNewPress()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((true, true), filter.Key(true, 0, true, false));
        Assert.Equal((true, true), filter.Key(true, 5000, true, false));
    }

    [Fact]
    public void FilterSeparatesQuickConsecutivePresses()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((true, true), filter.Key(true, 0, true, false));
        Assert.Equal((true, false), filter.Key(false, 80, true, false));
        Assert.Equal((true, true), filter.Key(true, 160, true, false));
    }

    [Fact]
    public void FilterConsumesLateReleaseOfLongPressWithoutAutoRepeat()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((true, true), filter.Key(true, 0, true, false));
        Assert.Equal((true, false), filter.Key(false, 4000, false, false));
    }

    [Fact]
    public void FilterKeepsRepeatsStampedCloseTogetherAsRepeats()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((true, true), filter.Key(true, 0, true, false));
        // Timestamps come from the events, so a hook that runs late does not turn a repeat into a press.
        for (uint time = 500; time < 5000; time += 33) Assert.Equal((true, false), filter.Key(true, time, true, false));
        Assert.Equal((true, false), filter.Key(false, 5000, true, false));
    }

    [Fact]
    public void FilterHandlesTickCountWrap()
    {
        var filter = new EscapeKeyFilter();
        Assert.Equal((true, true), filter.Key(true, uint.MaxValue - 10, true, false));
        Assert.Equal((true, false), filter.Key(true, 20, true, false));
        Assert.Equal((true, true), filter.Key(true, 5000, true, false));
    }

    [Fact]
    public void MissingPreferencesDefaultToDoubleWithoutCreatingFiles()
    {
        var store = new EscapeCancelPreferencesStore(PreferencesPath);
        Assert.Equal(EscapeCancelBehavior.Double, store.Current);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(EscapeCancelBehavior.Double)]
    [InlineData(EscapeCancelBehavior.Single)]
    [InlineData(EscapeCancelBehavior.Instant)]
    public void SavedBehaviorSurvivesReload(EscapeCancelBehavior behavior)
    {
        Assert.Null(new EscapeCancelPreferencesStore(PreferencesPath).Save(behavior));
        Assert.Equal(behavior, new EscapeCancelPreferencesStore(PreferencesPath).Current);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("{\"Behavior\":\"Off\"}")]
    [InlineData("{\"Behavior\":1}")]
    [InlineData("{\"Behavior\":\"1\"}")]
    [InlineData("{}")]
    [InlineData("broken")]
    public void InvalidPreferencesUseDoubleAndCanBeRepaired(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, json);
        var store = new EscapeCancelPreferencesStore(PreferencesPath);
        Assert.Equal(EscapeCancelBehavior.Double, store.Current);
        Assert.NotNull(store.Error);
        Assert.Equal(json, File.ReadAllText(PreferencesPath));
        Assert.Null(store.Save(EscapeCancelBehavior.Single));
        Assert.Equal(EscapeCancelBehavior.Single, new EscapeCancelPreferencesStore(PreferencesPath).Current);
    }

    [Fact]
    public void InvalidBehaviorCannotReplaceSavedChoice()
    {
        var store = new EscapeCancelPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(EscapeCancelBehavior.Instant));
        Assert.NotNull(store.Save((EscapeCancelBehavior)99));
        Assert.Equal(EscapeCancelBehavior.Instant, new EscapeCancelPreferencesStore(PreferencesPath).Current);
    }

    [Theory]
    [InlineData(1, "PRESS ESC AGAIN")]
    [InlineData(2, "PRESS ESC AGAIN")]
    [InlineData(0, "PRESS ESC AGAIN")]
    [InlineData(5, "PRESS ESC AGAIN")]
    [InlineData(3, "ERROR")]
    public void CancelWarningReplacesLabelUnlessDictationFailed(int phase, string label)
    {
        var state = new DictationOverlayState((DictationPhase)phase, TimeSpan.Zero, "Status", "Notepad", CancelWarning: "Press Esc again to cancel recording");
        Assert.Equal(label, state.Label);
        Assert.Equal(state.ShowsCancelWarning ? "Press Esc again to cancel recording" : "Status", state.AccessibleMessage);
    }

    [Theory]
    [InlineData(0, "CANCELLED")]
    [InlineData(1, "RECORDING")]
    public void CancelledBannerShowsOnlyWhileIdle(int phase, string label)
    {
        var state = new DictationOverlayState((DictationPhase)phase, TimeSpan.Zero, "Status", "Notepad", Cancelled: true);
        Assert.Equal(label, state.Label);
        Assert.Equal(phase == 0, state.ShowsCancelled);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
