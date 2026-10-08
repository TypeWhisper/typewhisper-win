using System.Net;
using System.Net.Http.Headers;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace PortableMigration.Tests;

// Runs the real plugin through the host retry/chunk pipeline, with HTTP confined to a fake handler.
public abstract class TranscriptionResilienceTests
{
    protected abstract ITranscriptionEnginePlugin Create(HttpClient client);

    private async Task<ITranscriptionEnginePlugin> Activate(HttpClient client)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.LoadSecretAsync("api-key")).ReturnsAsync("fixture-key");
        var engine = Create(client);
        await engine.ActivateAsync(host.Object);
        return engine;
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication, false)]
    [InlineData(403, PluginRequestFailureKind.Permission, false)]
    [InlineData(404, PluginRequestFailureKind.InvalidRequest, false)]
    [InlineData(408, PluginRequestFailureKind.Timeout, true)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge, false)]
    [InlineData(429, PluginRequestFailureKind.RateLimit, true)]
    [InlineData(500, PluginRequestFailureKind.ServerError, true)]
    [InlineData(502, PluginRequestFailureKind.ServerError, true)]
    [InlineData(503, PluginRequestFailureKind.ServerError, true)]
    [InlineData(504, PluginRequestFailureKind.ServerError, true)]
    public async Task HttpErrorsExposeStatusKindAndRetryDelay(int status, PluginRequestFailureKind kind, bool transient)
    {
        using var client = new HttpClient(new Handler((_, _) => Response(status)));
        var engine = await Activate(client);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            engine.TranscribeAsync(PcmWaveEncoder.Encode([0f]), "en", false, null, default));
        Assert.Equal(status, error.HttpStatusCode);
        Assert.Equal(kind, error.FailureKind);
        Assert.Equal(transient, error.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(2), error.RetryAfter);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public async Task RetryAfterHttpDatesArePreservedAndPastDatesAreClamped(int seconds)
    {
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var response = Response(429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(seconds));
            return response;
        }));
        var engine = await Activate(client);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            engine.TranscribeAsync(PcmWaveEncoder.Encode([0f]), "en", false, null, default));
        if (seconds < 0) Assert.Equal(TimeSpan.Zero, error.RetryAfter);
        else Assert.InRange(error.RetryAfter!.Value.TotalSeconds, 25, 30);
    }

    [Fact]
    public async Task HostSplitsRejectedAudioAndRetriesOnlyTheRateLimitedChunk()
    {
        var uploads = new List<byte[]>();
        using var client = new HttpClient(new Handler((request, body) =>
        {
            uploads.Add(request.Content!.Headers.ContentType!.MediaType == "audio/wav" ? body[44..] : body);
            return Response(uploads.Count switch { 1 => 413, 2 => 429, _ => 200 });
        }));
        var engine = await Activate(client);
        var samples = Enumerable.Repeat(0.5f, 32000).ToArray();
        var delays = new List<TimeSpan>();
        var result = await LanguageHintTranscription.DecodeAsync(engine, samples, () => PcmWaveEncoder.Encode(samples), "en", [], false, default,
            retry: PluginRequestRetry.Create(engine.PluginId, (delay, _) => { delays.Add(delay); return Task.CompletedTask; }));
        Assert.Equal("part part", result.Text);
        Assert.Equal([64000, 32000, 32000, 32000], uploads.Select(bytes => bytes.Length));
        Assert.Equal(uploads[1], uploads[2]);
        Assert.Equal(uploads[0], uploads[2].Concat(uploads[3]).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(delays));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task HostDoesNotRetryPermanentErrors(int status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Response(status); }));
        var engine = await Activate(client);
        await Assert.ThrowsAsync<PluginRequestException>(() => LanguageHintTranscription.DecodeAsync(engine, new float[1],
            () => PcmWaveEncoder.Encode([0f]), "en", [], false, default,
            retry: PluginRequestRetry.Create(engine.PluginId, (_, _) => throw new Exception("Must not retry."))));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostRetriesNetworkFailuresAndTimeouts(bool timeout)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            if (++calls == 1)
            {
                if (timeout) throw new TaskCanceledException("HTTP timeout");
                throw new HttpRequestException("Offline");
            }
            return Response(200);
        }));
        var engine = await Activate(client);
        var result = await LanguageHintTranscription.DecodeAsync(engine, new float[1], () => PcmWaveEncoder.Encode([0f]), "en", [], false, default,
            retry: PluginRequestRetry.Create(engine.PluginId, (_, _) => Task.CompletedTask));
        Assert.Equal("part", result.Text);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CallerCancellationIsNeverRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));
        var engine = await Activate(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LanguageHintTranscription.DecodeAsync(engine, new float[1],
            () => PcmWaveEncoder.Encode([0f]), "en", [], false, cancellation.Token,
            retry: PluginRequestRetry.Create(engine.PluginId, (_, _) => throw new Exception("Must not retry cancellation."))));
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage Response(int status)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(status == 200 ? """{"text":"part","transcription":"part"}""" : """{"message":"details"}""")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, await request.Content!.ReadAsByteArrayAsync(ct));
    }
}
