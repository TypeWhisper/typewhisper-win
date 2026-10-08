using System.Buffers.Binary;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed class ChunkedTranscriptionTests
{
    [Fact]
    public async Task SplitsBeforeAllocatingTheWholeWavAndOffsetsResultTimes()
    {
        var engine = Engine(52); // Four samples plus the WAV header.
        float[] samples = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f];
        var uploads = new List<byte[]>();
        engine.Setup(e => e.TranscribeAsync(It.IsAny<byte[]>(), "de", false, null, It.IsAny<CancellationToken>()))
            .Returns((byte[] wav, string? _, bool _, string? _, CancellationToken _) =>
            {
                uploads.Add(wav);
                return Task.FromResult(new PluginTranscriptionResult($" Part {uploads.Count} ", "de", 0, 0.8f)
                {
                    Segments = [new("segment", 0, 1) { NoSpeechProbability = uploads.Count == 2 ? null : uploads.Count / 10f }],
                    TokenTimings = [new("token", 0, 1)]
                });
            });

        var result = await LanguageHintTranscription.DecodeAsync(engine.Object, samples,
            () => throw new Exception("The oversized WAV must never be allocated"), "de", [], false, default);

        Assert.Equal([52, 52, 46], uploads.Select(wav => wav.Length));
        Assert.Equal(PcmWaveEncoder.Encode(samples).Skip(44), uploads.SelectMany(wav => wav.Skip(44)));
        Assert.Equal("Part 1 Part 2 Part 3", result.Text);
        Assert.Equal(samples.Length / 16000d, result.DurationSeconds);
        Assert.Equal([0, 4 / 16000d, 8 / 16000d], result.Segments.Select(segment => segment.Start));
        Assert.Equal(result.Segments.Select(segment => segment.Start), result.TokenTimings.Select(token => token.StartSeconds));
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(0.8f, result.NoSpeechProbability);
        Assert.Equal([0.1f, null, 0.3f], result.Segments.Select(segment => segment.NoSpeechProbability));
    }

    [Fact]
    public async Task KeepsLanguageHintsAndRetriesOnlyTheFailedChunk()
    {
        var engine = Engine(48);
        engine.SetupGet(e => e.SupportsLanguageHints).Returns(true);
        engine.SetupGet(e => e.SupportedLanguages).Returns([]);
        string[] hints = ["de", "en"];
        var attempts = new List<byte[]>();
        engine.Setup(e => e.TranscribeWithLanguageHintsAsync(It.IsAny<byte[]>(), hints, false, null, It.IsAny<CancellationToken>()))
            .Returns((byte[] wav, IReadOnlyList<string> _, bool _, string? _, CancellationToken _) =>
            {
                attempts.Add(wav);
                if (attempts.Count == 2)
                    throw new PluginRequestException("busy", PluginRequestFailureKind.RateLimit, 429);
                return Task.FromResult(new PluginTranscriptionResult("text", "de", 1, null));
            });
        var result = await LanguageHintTranscription.DecodeAsync(engine.Object, new float[4],
            () => throw new Exception("No whole upload"), null, hints, false, default,
            retry: PluginRequestRetry.Create(null, (_, _) => Task.CompletedTask));

        Assert.Equal("text text", result.Text);
        Assert.Equal(3, attempts.Count);
        Assert.NotSame(attempts[0], attempts[1]);
        Assert.Same(attempts[1], attempts[2]);
    }

    [Fact]
    public async Task SplitsAgainWhenTheProviderReturns413()
    {
        var engine = Engine(int.MaxValue);
        var sizes = new List<int>();
        engine.Setup(e => e.TranscribeAsync(It.IsAny<byte[]>(), null, false, null, It.IsAny<CancellationToken>()))
            .Returns((byte[] wav, string? _, bool _, string? _, CancellationToken _) =>
            {
                sizes.Add(wav.Length);
                if (sizes.Count == 1)
                    throw new PluginRequestException("limit", PluginRequestFailureKind.RequestTooLarge, 413);
                return Task.FromResult(new PluginTranscriptionResult("part", "en", 1, null));
            });
        var samples = Enumerable.Repeat(0.5f, 32000).ToArray();

        var result = await LanguageHintTranscription.DecodeAsync(engine.Object, samples,
            () => PcmWaveEncoder.Encode(samples), null, [], false, default);

        Assert.Equal([64044, 32044, 32044], sizes);
        Assert.Equal("part part", result.Text);
        Assert.Equal(2, result.DurationSeconds);
    }

    [Fact]
    public async Task CancellationAndLaterFailureNeverReturnPartialText()
    {
        var engine = Engine(48);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        engine.Setup(e => e.TranscribeAsync(It.IsAny<byte[]>(), null, false, null, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                cancellation.Cancel();
                return Task.FromResult(new PluginTranscriptionResult("partial", "en", 1, null));
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LanguageHintTranscription.DecodeAsync(
            engine.Object, new float[4], () => [], null, [], false, cancellation.Token));
        Assert.Equal(1, calls);

        engine.SetupSequence(e => e.TranscribeAsync(It.IsAny<byte[]>(), null, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginTranscriptionResult("partial", "en", 1, null))
            .ThrowsAsync(new PluginRequestException("key", PluginRequestFailureKind.Authentication, 401));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => LanguageHintTranscription.DecodeAsync(
            engine.Object, new float[4], () => [], null, [], false, default));
        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
    }

    [Fact]
    public async Task QuietBoundaryPreservesEverySampleExactlyOnce()
    {
        var engine = Engine(64044);
        var samples = Enumerable.Repeat(0.5f, 48000).ToArray();
        Array.Clear(samples, 28000, 1600);
        var uploads = new List<byte[]>();
        engine.Setup(e => e.TranscribeAsync(It.IsAny<byte[]>(), null, false, null, It.IsAny<CancellationToken>()))
            .Returns((byte[] wav, string? _, bool _, string? _, CancellationToken _) =>
            {
                uploads.Add(wav);
                return Task.FromResult(new PluginTranscriptionResult("part", "en", 1, null));
            });
        await LanguageHintTranscription.DecodeAsync(engine.Object, samples, () => [], null, [], false, default);

        Assert.Equal(2, uploads.Count);
        Assert.InRange(BinaryPrimitives.ReadInt32LittleEndian(uploads[0].AsSpan(40)) / 2, 28000, 29600);
        Assert.Equal(PcmWaveEncoder.Encode(samples).Skip(44), uploads.SelectMany(wav => wav.Skip(44)));
    }

    private static Mock<ITranscriptionEnginePlugin> Engine(int maximumBytes)
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.MaximumAudioUploadBytes).Returns(maximumBytes);
        return engine;
    }
}
