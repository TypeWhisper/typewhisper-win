using TypeWhisper.WinUI;
using Xunit;

public sealed class SilenceAutoStopTests
{
    [Fact]
    public void InitialSilenceStopsAtTimeoutNotBefore()
    {
        var detector = new SilenceAutoStop(TimeSpan.FromSeconds(3));
        Assert.False(detector.ShouldStop(TimeSpan.FromMilliseconds(2999), false));
        Assert.True(detector.ShouldStop(TimeSpan.FromSeconds(3), false));
    }

    [Fact]
    public void SpeechResetsSilenceButLowNoiseDoesNot()
    {
        var detector = new SilenceAutoStop(TimeSpan.FromSeconds(3));
        detector.Observe(TimeSpan.FromSeconds(2), .1f);
        detector.Observe(TimeSpan.FromSeconds(4), .001f);
        Assert.False(detector.ShouldStop(TimeSpan.FromSeconds(4), false));
        Assert.True(detector.ShouldStop(TimeSpan.FromSeconds(5), false));
    }

    [Fact]
    public void HeldModifiersDelayStopUntilRelease()
    {
        var detector = new SilenceAutoStop(TimeSpan.FromSeconds(3));
        Assert.False(detector.ShouldStop(TimeSpan.FromSeconds(10), true));
        Assert.True(detector.ShouldStop(TimeSpan.FromSeconds(10), false));
    }

    [Fact]
    public void NewRecordingDoesNotInheritPreviousActivity()
    {
        var previous = new SilenceAutoStop(TimeSpan.FromSeconds(3));
        previous.Observe(TimeSpan.FromSeconds(100), .1f);
        Assert.True(new SilenceAutoStop(TimeSpan.FromSeconds(3)).ShouldStop(TimeSpan.FromSeconds(3), false));
    }

    [Fact]
    public void InvalidOrOutOfOrderLevelsDoNotMoveActivityBackwards()
    {
        var detector = new SilenceAutoStop(TimeSpan.FromSeconds(3));
        detector.Observe(TimeSpan.FromSeconds(4), .1f);
        detector.Observe(TimeSpan.FromSeconds(2), .1f);
        detector.Observe(TimeSpan.FromSeconds(6), float.NaN);
        Assert.False(detector.ShouldStop(TimeSpan.FromSeconds(6), false));
        Assert.True(detector.ShouldStop(TimeSpan.FromSeconds(7), false));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(9999, 3600)]
    public void TimeoutIsValidated(int input, int expected)
        => Assert.Equal(expected, new DictationAudioPreferences { SilenceAutoStopSeconds = input }.Validated().SilenceAutoStopSeconds);

    [Fact]
    public void ExistingPreferencesDefaultToDisabledAndTenSeconds()
    {
        var preferences = System.Text.Json.JsonSerializer.Deserialize<DictationAudioPreferences>("{}")!;
        Assert.False(preferences.SilenceAutoStopEnabled);
        Assert.Equal(10, preferences.SilenceAutoStopSeconds);
    }
}
