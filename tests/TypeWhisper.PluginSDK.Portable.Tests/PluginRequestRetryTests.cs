using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed class PluginRequestRetryTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    [Fact]
    public void ClassifiesTransientKindsAndOverloadStatusesOnly()
    {
        var rateLimit = PluginRequestRetry.Classify(new PluginRequestException("slow down", PluginRequestFailureKind.RateLimit, 429, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(3), rateLimit!.RetryAfter);
        Assert.Equal(429, rateLimit.StatusCode);
        Assert.Equal("RateLimit", rateLimit.Kind);
        // A plugin may classify a gateway status as something else; the status itself still means overload.
        Assert.NotNull(PluginRequestRetry.Classify(new PluginRequestException("gateway", PluginRequestFailureKind.InvalidRequest, 503, isTransient: false)));
        Assert.NotNull(PluginRequestRetry.Classify(new PluginRequestException("down", PluginRequestFailureKind.ServerError)));
        Assert.Null(PluginRequestRetry.Classify(new PluginRequestException("too big", PluginRequestFailureKind.RequestTooLarge, 413)));
        Assert.Null(PluginRequestRetry.Classify(new PluginRequestException("key", PluginRequestFailureKind.Authentication, 401)));
        Assert.Null(PluginRequestRetry.Classify(new PluginRequestException("quiet", PluginRequestFailureKind.Network, isTransient: false)));
        Assert.Null(PluginRequestRetry.Classify(new InvalidOperationException("plain")));
    }

    [Fact]
    public async Task TranscriptionUploadIsSentAgainAfterATransientFailure()
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.PluginId).Returns("com.example.cloud");
        byte[] wav = [1, 2, 3];
        var response = new PluginTranscriptionResult("Hello", "en", 1, null);
        engine.SetupSequence(e => e.TranscribeAsync(wav, "en", false, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PluginRequestException("busy", PluginRequestFailureKind.ServerError, 503, TimeSpan.FromSeconds(2)))
            .ReturnsAsync(response);
        var stages = new List<string>();
        var errors = new List<Exception?>();
        PluginHostDiagnostics.Sink = (stage, error) => { stages.Add(stage); errors.Add(error); };
        try
        {
            var actual = await LanguageHintTranscription.DecodeAsync(engine.Object, new float[1], () => wav, "en", [], false, default,
                retry: PluginRequestRetry.Create("com.example.cloud", NoWait));

            Assert.Same(response, actual);
            engine.Verify(e => e.TranscribeAsync(wav, "en", false, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
            var stage = Assert.Single(stages);
            Assert.Equal("cloud.retry plugin=com.example.cloud attempt=1 delayMs=2000 kind=ServerError status=503", stage);
            Assert.IsType<PluginRequestException>(Assert.Single(errors));
        }
        finally { PluginHostDiagnostics.Sink = null; }
    }

    [Fact]
    public async Task HintedUploadSurfacesANonTransientFailureAtOnce()
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsLanguageHints).Returns(true);
        engine.SetupGet(e => e.SupportedLanguages).Returns([]);
        string[] hints = ["de", "en"];
        byte[] wav = [1];
        engine.Setup(e => e.TranscribeWithLanguageHintsAsync(wav, hints, false, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PluginRequestException("key", PluginRequestFailureKind.Authentication, 401));

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => LanguageHintTranscription.DecodeAsync(engine.Object, new float[1], () => wav,
            null, hints, false, default, retry: PluginRequestRetry.Create(null, NoWait)));

        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
        engine.Verify(e => e.TranscribeWithLanguageHintsAsync(wav, hints, false, null, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UploadIsGivenUpWhenTheProviderAsksForALongWait()
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        byte[] wav = [1];
        engine.Setup(e => e.TranscribeAsync(wav, null, false, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PluginRequestException("later", PluginRequestFailureKind.RateLimit, 429, TimeSpan.FromMinutes(1)));

        await Assert.ThrowsAsync<PluginRequestException>(() => LanguageHintTranscription.DecodeAsync(engine.Object, new float[1], () => wav,
            null, [], false, default, retry: PluginRequestRetry.Create(null, NoWait)));

        engine.Verify(e => e.TranscribeAsync(wav, null, false, null, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task LocalPcmEnginesBypassThePolicy()
    {
        var engine = new Mock<IPcmTranscriptionEnginePlugin>();
        float[] samples = [0.5f];
        engine.Setup(e => e.TranscribePcmAsync(samples, null, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PluginRequestException("worker", PluginRequestFailureKind.Timeout));

        await Assert.ThrowsAsync<PluginRequestException>(() => LanguageHintTranscription.DecodeAsync(engine.Object, samples, () => [1],
            null, [], false, default, retry: PluginRequestRetry.Create(null, NoWait)));

        engine.Verify(e => e.TranscribePcmAsync(samples, null, false, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ChatCompletionIsSentAgainAfterARateLimit()
    {
        var provider = new Mock<ILlmProviderPlugin>();
        provider.SetupSequence(p => p.ProcessAsync("system", "text", "model", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PluginRequestException("slow down", PluginRequestFailureKind.RateLimit, 429))
            .ReturnsAsync("Processed");

        var result = await PluginRequestRetry.RunAsync("com.example.llm",
            token => provider.Object.ProcessAsync("system", "text", "model", token), default, PluginRequestRetry.Create("com.example.llm", NoWait));

        Assert.Equal("Processed", result);
        provider.Verify(p => p.ProcessAsync("system", "text", "model", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
