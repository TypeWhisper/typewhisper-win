using System.Diagnostics;
using NAudio.Wave;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using TypeWhisper.WinUI.Platform;
using Xunit;
using Xunit.Abstractions;

// Opt-in integration checks: use existing assets/credentials, never download or modify them.
public sealed class ProviderStartupAudioTests(ITestOutputHelper output)
{
    [ProviderStartupFact("LOCAL")]
    [Trait("Category", "ProviderStartup")]
    public async Task ColdLocalModelLoadsAfterCaptureStartsAndPreservesFirstWords()
    {
        var data = Path.Combine(Path.GetTempPath(), "typewhisper-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var host = new VocabularyHostServices(data, assetDirectory: Required("LOCAL_ASSETS"));
            host.SetSetting("selectedModel", Required("LOCAL_MODEL"));
            await using var package = await PortablePluginPackage.LoadAsync(Required("LOCAL_PACKAGE"), host, new(1, 1, 3));
            var engine = Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(package.Plugin);
            var pcm = Speech();
            var input = new ImmediateAudioTests.ReplayInput();
            using var capture = Capture(input);
            capture.StartRecording(enableRecovery: false);
            Feed(input, pcm[..9600]);
            Assert.True(capture.IsRecording);
            var timer = Stopwatch.StartNew();
            // New package instance: no LoadModelAsync or decode has happened before microphone start.
            var load = Task.Run(() => engine.LoadModelAsync(Required("LOCAL_MODEL"), default));
            Feed(input, pcm[9600..]);
            var samples = capture.StopRecording()!;
            AssertSamples(pcm, samples);
            await load.WaitAsync(TimeSpan.FromMinutes(3));
            output.WriteLine($"Cold model load after capture start: {timer.ElapsedMilliseconds} ms; retained {samples.Length} samples.");
            var result = await engine.TranscribePcmAsync(samples, "de", false, default).WaitAsync(TimeSpan.FromMinutes(3));
            output.WriteLine("Local: " + result.Text);
            Assert.StartsWith("morgen", result.Text.Trim().ToLowerInvariant());
            Assert.Contains("schritte", result.Text.ToLowerInvariant());
            var delayed = DelayedCapture(pcm);
            var before = await engine.TranscribePcmAsync(delayed, "de", false, default).WaitAsync(TimeSpan.FromMinutes(3));
            output.WriteLine($"Before (simulated 300 ms capture delay): {before.Text}; missing {samples.Length - delayed.Length} samples. After: {result.Text}");
        }
        finally { Directory.Delete(data, recursive: true); }
    }

    [ProviderStartupFact("CLOUD")]
    [Trait("Category", "ProviderStartup")]
    public async Task RealCloudConnectionReceivesTheBufferedBeginningAndCompleteRecording()
    {
        var data = Path.Combine(Path.GetTempPath(), "typewhisper-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var source = Required("CLOUD_DATA");
            File.Copy(Path.Combine(source, "settings.json"), Path.Combine(data, "settings.json"));
            var host = new VocabularyHostServices(data, secrets: new WindowsPluginSecretStore(source));
            await using var package = await PortablePluginPackage.LoadAsync(Required("CLOUD_PACKAGE"), host, new(1, 1, 3));
            var engine = Assert.IsAssignableFrom<ITranscriptionEnginePlugin>(package.Plugin);
            output.WriteLine($"Cloud configured={engine.IsConfigured}, streaming={engine.SupportsStreaming}, completion={engine.SupportsStreamingCompletion}, model={engine.SelectedModelId}");
            var pcm = Speech();
            var input = new ImmediateAudioTests.ReplayInput();
            using var capture = Capture(input);
            var buffer = new BufferedAudioHandoff();
            buffer.Begin();
            capture.SamplesAvailable += (_, e) => buffer.Append(e.Samples);
            capture.StartRecording(enableRecovery: false);
            Feed(input, pcm[..9600]); // First 600 ms arrive before the host creates the stream.
            var failed = false;
            await using var stream = new StreamingDictation(async (use, ct) =>
            {
                await Task.Delay(600, ct); // Additional provider acquisition/connection delay.
                try { return await use(engine, ct); }
                catch (Exception ex)
                {
                    output.WriteLine($"Cloud failure: {ex.GetType().Name}; status={(ex as PluginRequestException)?.HttpStatusCode}; kind={(ex as PluginRequestException)?.FailureKind}");
                    if (ex is System.Net.WebSockets.WebSocketException socket)
                        output.WriteLine($"WebSocket: {socket.WebSocketErrorCode}; {socket.Message}; {socket.StackTrace}");
                    throw;
                }
            }, ["de"], _ => { }, () => failed = true, default);
            Assert.True(buffer.Attach(samples => stream.Append(samples)));
            for (var offset = 9600; offset < pcm.Length; offset += 1600)
            {
                input.Feed(pcm[offset..Math.Min(offset + 1600, pcm.Length)]);
                await Task.Delay(100);
            }
            var samples = capture.StopRecording()!;
            AssertSamples(pcm, samples);
            var text = await stream.FinishAsync(samples.Length);
            buffer.Reset();
            Assert.False(failed);
            Assert.NotNull(text);
            output.WriteLine($"Cloud: {text}; retained {samples.Length} samples including the first 600 ms.");
            Assert.StartsWith("morgen", text.Trim().ToLowerInvariant());
            Assert.Contains("schritte", text.ToLowerInvariant());
            var delayed = DelayedCapture(pcm);
            await using var beforeStream = new StreamingDictation((use, ct) => use(engine, ct), ["de"], _ => { }, () => { }, default);
            for (var offset = 0; offset < delayed.Length; offset += 1600)
            {
                beforeStream.Append(delayed.AsSpan(offset, Math.Min(1600, delayed.Length - offset)));
                await Task.Delay(100);
            }
            var before = await beforeStream.FinishAsync(delayed.Length);
            Assert.NotNull(before);
            output.WriteLine($"Before (simulated 300 ms capture delay): {before}; missing {samples.Length - delayed.Length} samples. After: {text}");
        }
        finally { Directory.Delete(data, recursive: true); }
    }

    private static AudioRecordingService Capture(ImmediateAudioTests.ReplayInput input)
    {
        var capture = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), input, Timeout.InfiniteTimeSpan) { NormalizationEnabled = false };
        Assert.True(capture.WarmUp());
        Assert.False(input.Running);
        return capture;
    }
    private static void Feed(ImmediateAudioTests.ReplayInput input, short[] pcm)
    {
        for (var offset = 0; offset < pcm.Length; offset += 480)
            input.Feed(pcm[offset..Math.Min(offset + 480, pcm.Length)]);
    }
    private static void AssertSamples(short[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i] / 32768f, actual[i]);
    }
    private static float[] DelayedCapture(short[] pcm)
    {
        var input = new ImmediateAudioTests.ReplayInput();
        using var capture = Capture(input);
        Feed(input, pcm[..4800]); // User speaks while host preparation still blocks microphone start.
        capture.StartRecording(enableRecovery: false);
        Feed(input, pcm[4800..]);
        var samples = capture.StopRecording()!;
        AssertSamples(pcm[4800..], samples);
        return samples;
    }
    private static short[] Speech()
    {
        using var wave = new WaveFileReader(Required("WAV"));
        Assert.Equal(WaveFormatEncoding.Pcm, wave.WaveFormat.Encoding);
        Assert.Equal(16000, wave.WaveFormat.SampleRate);
        Assert.Equal(16, wave.WaveFormat.BitsPerSample);
        Assert.Equal(1, wave.WaveFormat.Channels);
        var bytes = new byte[checked((int)wave.Length)];
        wave.ReadExactly(bytes);
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        var first = Array.FindIndex(pcm, sample => Math.Abs((int)sample) > 64);
        Assert.True(first >= 0);
        return pcm[first..]; // No leading silence to hide a clipped first word.
    }
    private static string Required(string suffix) => Environment.GetEnvironmentVariable("TYPEWHISPER_STARTUP_" + suffix)
        ?? throw new InvalidOperationException("Missing startup-test configuration: " + suffix);
}

public sealed class ProviderStartupFactAttribute : FactAttribute
{
    public ProviderStartupFactAttribute(string kind)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_STARTUP_" + kind + "_PACKAGE")))
            Skip = "Opt-in test: provide existing provider package, local assets or cloud credentials through TYPEWHISPER_STARTUP_* variables.";
    }
}
