using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class SimpleVoiceActivityDetectorTests
{
    [Fact]
    public void AcceptWaveform_QueuesSpeechSegmentAfterSilence()
    {
        var sut = new SimpleVoiceActivityDetector(
            sampleRate: 10,
            speechThreshold: 0.1f,
            minSpeechDuration: TimeSpan.FromMilliseconds(300),
            minSilenceDuration: TimeSpan.FromMilliseconds(200));

        sut.AcceptWaveform([0.2f, 0.2f, 0.2f, 0.0f, 0.0f]);

        Assert.False(sut.IsEmpty());
        var segment = sut.Front();
        Assert.Equal([0.2f, 0.2f, 0.2f], segment.Samples);
    }

    [Fact]
    public void Flush_DiscardsShortSpeechSegment()
    {
        var sut = new SimpleVoiceActivityDetector(
            sampleRate: 10,
            speechThreshold: 0.1f,
            minSpeechDuration: TimeSpan.FromMilliseconds(300),
            minSilenceDuration: TimeSpan.FromMilliseconds(200));

        sut.AcceptWaveform([0.2f, 0.2f]);
        sut.Flush();

        Assert.True(sut.IsEmpty());
        Assert.Equal(1, sut.DiscardedShortSegmentCount);
    }

    [Fact]
    public void AcceptWaveform_QueuesSegmentAtMaxDuration()
    {
        var sut = new SimpleVoiceActivityDetector(
            sampleRate: 10,
            speechThreshold: 0.1f,
            minSpeechDuration: TimeSpan.FromMilliseconds(300),
            minSilenceDuration: TimeSpan.FromMilliseconds(200),
            maxSegmentDuration: TimeSpan.FromMilliseconds(500));

        sut.AcceptWaveform([0.2f, 0.2f, 0.2f, 0.2f, 0.2f]);

        Assert.False(sut.IsEmpty());
        Assert.Equal(5, sut.Front().Samples.Length);
    }

    [Fact]
    public void Constructor_RejectsInvalidSampleRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimpleVoiceActivityDetector(sampleRate: 0));
    }

    [Fact]
    public void Methods_ThrowAfterDispose()
    {
        var sut = new SimpleVoiceActivityDetector();

        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sut.AcceptWaveform([0.2f]));
        Assert.Throws<ObjectDisposedException>(() => sut.Flush());
        Assert.Throws<ObjectDisposedException>(() => sut.IsEmpty());
        Assert.Throws<ObjectDisposedException>(() => { _ = sut.DiscardedShortSegmentCount; });
    }

    [Fact]
    public void AcceptWaveform_TrimsLeadingSilenceBeforeSpeech()
    {
        var sut = new SimpleVoiceActivityDetector(
            sampleRate: 10,
            speechThreshold: 0.1f,
            minSpeechDuration: TimeSpan.FromMilliseconds(300),
            minSilenceDuration: TimeSpan.FromMilliseconds(200));

        sut.AcceptWaveform([0.0f, 0.0f, 0.2f, 0.2f, 0.2f, 0.0f, 0.0f]);

        Assert.False(sut.IsEmpty());
        Assert.Equal([0.2f, 0.2f, 0.2f], sut.Front().Samples);
    }
}
