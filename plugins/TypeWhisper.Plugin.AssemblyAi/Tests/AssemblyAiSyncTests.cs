using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK;

public partial class AssemblyAiTests
{
    /// <summary>A mono PCM16 WAV at 16 kHz.</summary>
    private static byte[] Wav(double seconds, int sampleRate = 16000)
    {
        var data = (int)(seconds * sampleRate) * 2;
        var wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data); "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16); "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data);
        return wav;
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData(" auto ", null)]
    [InlineData(null, null)]
    public async Task SyncApiReturnsTheTranscriptInOneRequest(string? language, string? expectedLanguage)
    {
        var audio = Wav(2.5);
        var calls = 0;
        using var plugin = Plugin(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://sync.assemblyai.com/v1/transcribe", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.Equal("universal-3-5-pro", Assert.Single(request.Headers.GetValues("X-AAI-Model")));
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal("audio", parts[0].Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("audio/wav", parts[0].Headers.ContentType!.MediaType); Assert.Equal(audio, await parts[0].ReadAsByteArrayAsync(ct));
            Assert.Equal("config", parts[1].Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("application/json", parts[1].Headers.ContentType!.MediaType);
            var config = JsonSerializer.Deserialize<JsonElement>(await parts[1].ReadAsStringAsync(ct));
            Assert.Equal(new[] { "Grüße", "TypeWhisper" }, config.GetProperty("keyterms_prompt").EnumerateArray().Select(e => e.GetString()));
            if (expectedLanguage is null) Assert.False(config.TryGetProperty("language_codes", out _));
            else Assert.Equal(expectedLanguage, Assert.Single(config.GetProperty("language_codes").EnumerateArray()).GetString());
            return Json("""{"text":"Grüße TypeWhisper!","words":[{"text":"Grüße","confidence":0.9}],"audio_duration_ms":2500,"request_time_ms":240.5}""");
        });
        await plugin.ActivateAsync(Host());
        var result = await plugin.TranscribeAsync(audio, language, false, "Grüße, TypeWhisper, grüße", default);
        Assert.Equal(1, calls); Assert.Equal("Grüße TypeWhisper!", result.Text);
        Assert.Equal(expectedLanguage, result.DetectedLanguage); Assert.Equal(2.5, result.DurationSeconds); Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task SyncApiWithoutLanguageOrTermsSendsOnlyTheAudio()
    {
        using var plugin = Plugin((request, _) =>
        {
            Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content));
            return Task.FromResult(Json("""{"text":null}"""));
        });
        await plugin.ActivateAsync(Host());
        var result = await plugin.TranscribeAsync(Wav(1), null, false, null, default);
        Assert.Empty(result.Text); Assert.Equal(1, result.DurationSeconds);
    }

    [Theory]
    [InlineData(500)] [InlineData(429)] [InlineData(413)] [InlineData(400)] [InlineData(403)] [InlineData(0)] [InlineData(-1)]
    public async Task FailedSyncRequestFallsBackToTheAsyncApiWithoutRetrying(int failure)
    {
        var hosts = new List<string>();
        using var plugin = Plugin(async (request, ct) =>
        {
            hosts.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            switch (hosts.Count)
            {
                case 1:
                    if (failure == 0) throw new HttpRequestException("connection reset");
                    if (failure == -1) return Json("not json");
                    return Json("""{"detail":"private recording"}""", (HttpStatusCode)failure);
                case 2: return Json("""{"upload_url":"https://cdn.assemblyai.com/upload/fixture"}""");
                case 3:
                    var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal("de", body.GetProperty("language_code").GetString());
                    return Json("""{"id":"fixture-id"}""");
                default: return Json("""{"status":"completed","text":"Hallo","language_code":"de"}""");
            }
        });
        await plugin.ActivateAsync(Host());
        var result = await plugin.TranscribeAsync(Wav(1), "de", false, null, default);
        Assert.Equal("Hallo", result.Text);
        Assert.Equal(new[] { "sync.assemblyai.com/v1/transcribe", "api.assemblyai.com/v2/upload", "api.assemblyai.com/v2/transcript", "api.assemblyai.com/v2/transcript/fixture-id" }, hosts);
    }

    [Fact]
    public async Task UserCancellationDuringTheSyncRequestDoesNotFallBack()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var plugin = Plugin(async (_, ct) =>
        {
            calls++; started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        });
        await plugin.ActivateAsync(Host());
        var request = plugin.TranscribeAsync(Wav(1), null, false, null, cts.Token);
        await started.Task; cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvalidKeyOnTheSyncApiIsNotRetriedWithTheAsyncApi()
    {
        var calls = 0;
        using var plugin = Plugin((_, _) => { calls++; return Task.FromResult(Json("{}", HttpStatusCode.Unauthorized)); });
        await plugin.ActivateAsync(Host());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync(Wav(1), null, false, null, default));
        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("universal-2", "live", false, "de", 10, 1, false)]
    [InlineData("universal-3-5-pro", "async", false, "de", 10, 1, false)]
    [InlineData("universal-3-5-pro", "live", true, "de", 10, 1, false)]
    [InlineData("universal-3-5-pro", "sync", false, "xx", 10, 1, false)]
    [InlineData("universal-3-5-pro", "sync", false, "ko", 10, 1, true)]
    [InlineData("universal-3-5-pro", "sync", false, null, 1200, 100, true)]
    [InlineData("universal-3-5-pro", "live", false, null, 1201, 1, false)]
    [InlineData("universal-3-5-pro", "live", false, null, 10, 101, false)]
    public void SyncApiTakesOnlyWhatItAccepts(string model, string mode, bool diarization, string? language, int seconds, int terms, bool sync)
    {
        var prompt = string.Join(",", Enumerable.Range(0, terms).Select(i => "term" + i));
        var request = AssemblyAiPlugin.SyncRequestFor(AssemblyAiModels.Require(model), mode, diarization, language, prompt, TimeSpan.FromSeconds(seconds));
        Assert.Equal(sync, request is not null);
        if (request is not null) Assert.Equal(terms, request.Keyterms.Count);
    }

    [Fact]
    public void SyncApiTakesAtMost8000KeytermCharacters()
    {
        var model = AssemblyAiModels.All[0];
        string Prompt(int length) => string.Join(",", Enumerable.Range(0, 100).Select(i => i.ToString("D3") + new string('x', length - 3)));
        Assert.NotNull(AssemblyAiPlugin.SyncRequestFor(model, "live", false, null, Prompt(80), null));
        Assert.Null(AssemblyAiPlugin.SyncRequestFor(model, "live", false, null, Prompt(81), null));
    }

    [Fact]
    public void WavDurationReadsTheHeaderAndToleratesAnOpenDataSize()
    {
        Assert.Equal(TimeSpan.FromSeconds(2.5), AssemblyAiPlugin.WavDuration(Wav(2.5)));
        Assert.Equal(TimeSpan.FromSeconds(1), AssemblyAiPlugin.WavDuration(Wav(1, 48000)));
        var open = Wav(1); BinaryPrimitives.WriteUInt32LittleEndian(open.AsSpan(40), uint.MaxValue);
        Assert.Equal(TimeSpan.FromSeconds(1), AssemblyAiPlugin.WavDuration(open));
        Assert.Null(AssemblyAiPlugin.WavDuration([1, 2])); Assert.Null(AssemblyAiPlugin.WavDuration(Wav(1)[..36]));
    }

    [Theory]
    [InlineData("live", true)] [InlineData("sync", false)] [InlineData("async", false)]
    public async Task OnlyTheLiveModeStreamsAndTheModeSurvivesRestart(string mode, bool streams)
    {
        var host = Host();
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network"));
        await plugin.ActivateAsync(host);
        Assert.Equal("live", plugin.TextSettings.Single(s => s.Id == "transcriptionMode").Value);
        await plugin.SaveTextSettingAsync("transcriptionMode", mode, default);
        Assert.Equal(streams, plugin.SupportsStreaming); Assert.Equal(streams, plugin.SupportsStreamingPreviewForPrompt(string.Join(",", Enumerable.Range(0, 200).Select(i => "t" + i))));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("transcriptionMode", "fast", default));
        using var restart = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network"));
        await restart.ActivateAsync(host);
        Assert.Equal(mode, restart.TextSettings.Single(s => s.Id == "transcriptionMode").Value); Assert.Equal(streams, restart.SupportsStreaming);
    }

    [Fact]
    public async Task ProfilesFromBeforeTheModeSettingUseLive()
    {
        var host = Host();
        host.SetSetting("configuration", new { Model = "universal-3-5-pro", SpeakerDiarizationEnabled = false, SecretName = "api-key" });
        using var plugin = Plugin((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network"));
        await plugin.ActivateAsync(host);
        Assert.Equal("live", plugin.Mode); Assert.True(plugin.SupportsStreaming); Assert.True(plugin.IsConfigured);
    }
}
