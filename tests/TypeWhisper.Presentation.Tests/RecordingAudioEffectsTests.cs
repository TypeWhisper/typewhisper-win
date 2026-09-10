using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.WinUI;
using Xunit;

public sealed class RecordingAudioEffectsTests
{
    [Fact]
    public void RepeatedEndRestoresOnlyOnceAndUsesStartPreferences()
    {
        var duck = new Mock<IAudioDuckingService>();
        var media = new Mock<IMediaPauseService>();
        var effects = new RecordingAudioEffects(duck.Object, media.Object);
        effects.Begin(new() { AudioDuckingEnabled = true, PauseMediaDuringRecording = true, AudioDuckingLevel = .3f });
        effects.Begin(new());
        effects.End(); effects.End();
        duck.Verify(x => x.DuckAudio(.3f), Times.Once);
        duck.Verify(x => x.RestoreAudio(), Times.Once);
        media.Verify(x => x.PauseMedia(), Times.Once);
        media.Verify(x => x.ResumeMedia(), Times.Once);
    }

    [Fact]
    public void DisabledOptionsDoNotLowerOrToggleMedia()
    {
        var duck = new Mock<IAudioDuckingService>();
        var media = new Mock<IMediaPauseService>();
        var effects = new RecordingAudioEffects(duck.Object, media.Object);
        effects.End(); effects.Begin(new()); effects.End();
        duck.Verify(x => x.DuckAudio(It.IsAny<float>()), Times.Never);
        media.VerifyNoOtherCalls();
    }

    [Fact]
    public void StartFailureReleasesPreviouslyAcquiredEffects()
    {
        var duck = new Mock<IAudioDuckingService>();
        var media = new Mock<IMediaPauseService>();
        media.Setup(x => x.PauseMedia()).Throws<InvalidOperationException>();
        var effects = new RecordingAudioEffects(duck.Object, media.Object);
        Assert.Throws<InvalidOperationException>(() => effects.Begin(new() { AudioDuckingEnabled = true, PauseMediaDuringRecording = true }));
        effects.End();
        duck.Verify(x => x.RestoreAudio(), Times.Once);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    [InlineData(float.NaN, .2f)]
    public void InvalidVolumeIsNormalized(float value, float expected)
        => Assert.Equal(expected, new DictationAudioPreferences { AudioDuckingLevel = value }.Validated().AudioDuckingLevel);

    [Fact]
    public void PreferencesRoundTripPreservesStableDeviceIds()
    {
        var original = new DictationAudioPreferences { OutputDeviceId = "speakers", WhisperModeEnabled = true };
        Assert.Equal(original, System.Text.Json.JsonSerializer.Deserialize<DictationAudioPreferences>(System.Text.Json.JsonSerializer.Serialize(original)));
    }

    [Fact]
    public void LegacySeparateDuckingOutputIsIgnoredWhileSharedOutputIsPreserved()
    {
        var preferences = System.Text.Json.JsonSerializer.Deserialize<DictationAudioPreferences>(
            "{\"OutputDeviceId\":\"speakers\",\"DuckingDeviceId\":\"headset\"}")!;
        Assert.Equal("speakers", preferences.Validated().OutputDeviceId);
        Assert.DoesNotContain("DuckingDeviceId", System.Text.Json.JsonSerializer.Serialize(preferences));
    }
}
