using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using NAudio.Wave;
using SherpaOnnx;
using TypeWhisper.WinUI;
using TypeWhisper.Windows.Services;
using Xunit;
using Xunit.Abstractions;

public sealed class ImmediateAudioTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmediateCapturePreservesFirstAudioBlock(bool hold)
    {
        // Audible PCM marker from sample zero; no real device is opened.
        var pcm = Enumerable.Range(0, 16000).Select(i => (short)(6000 * Math.Cos(i * 0.1))).ToArray();
        var immediate = Capture(pcm, hold, false);
        var delayed = Capture(pcm, hold, true);
        Assert.Equal(pcm.Length, immediate.Length);
        Assert.Equal(pcm.Length - 4800, delayed.Length);
        for (var i = 0; i < pcm.Length; i++) Assert.Equal(pcm[i] / 32768f, immediate[i]);
        output.WriteLine($"Mode={(hold ? "hold" : "toggle")}: immediate={immediate.Length} samples; delayed={delayed.Length}; lost=4800 samples (300 ms).");
    }

    [Fact]
    [Trait("Category", "LocalParakeet")]
    public void GeneratedSpeechThroughCaptureAndParakeet()
    {
        var model = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_PARAKEET_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "Set TYPEWHISPER_TEST_PARAKEET_MODEL to an existing model directory; this test never downloads models.");
        using var synth = new SpeechSynthesizer();
        var voice = synth.GetInstalledVoices().First(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
        synth.SelectVoice(voice.VoiceInfo.Name);
        using var wave = new MemoryStream();
        synth.SetOutputToAudioStream(wave, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak("Bananas are yellow. This is a test of immediate recording.");
        synth.SetOutputToNull();
        var bytes = wave.ToArray();
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        // Remove synthesized leading silence so speech begins at the key-down boundary.
        var first = Array.FindIndex(pcm, sample => Math.Abs((int)sample) > 64);
        Assert.True(first >= 0);
        pcm = pcm[first..];
        var immediate = Capture(pcm, true, false);
        var delayed = Capture(pcm, true, true);
        Assert.Equal(pcm.Length, immediate.Length);
        Assert.Equal(4800, immediate.Length - delayed.Length);
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(model!, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(model!, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(model!, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(model!, "tokens.txt");
        config.ModelConfig.NumThreads = 4;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        using var recognizer = new OfflineRecognizer(config);
        string Decode(float[] samples)
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            recognizer.Decode(stream);
            return stream.Result.Text.Trim();
        }
        var actual = Decode(immediate);
        var old = Decode(delayed);
        output.WriteLine($"Voice: {voice.VoiceInfo.Name}\nImmediate: {actual}\nDelayed 300 ms: {old}");
        Assert.StartsWith("bananas", actual.ToLowerInvariant());
        Assert.Contains("immediate recording", actual.ToLowerInvariant());
    }

    private static float[] Capture(short[] pcm, bool hold, bool delayed)
    {
        var input = new ReplayInput();
        using var audio = new AudioRecordingService(new ReplayDevice(), input, Timeout.InfiniteTimeSpan) { NormalizationEnabled = false };
        Assert.True(audio.WarmUp());
        Assert.False(input.Running); // Preparation must not record.
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 0, bindings));
        if (!delayed) audio.StartRecording(enableRecovery: false);
        // Deterministic virtual time: same 30-ms blocks as the capture adapter.
        for (var offset = 0; offset < pcm.Length; offset += 480)
        {
            if (!hold && offset == 1920)
            {
                Assert.Null(state.Key(0xA0, false, 120, bindings, audio.IsRecording));
                Assert.Null(state.Key(0xA2, false, 120, bindings, audio.IsRecording));
            }
            if (delayed && offset == 4800) audio.StartRecording(enableRecovery: false);
            input.Feed(pcm.Skip(offset).Take(480).ToArray());
        }
        var endTime = pcm.Length / 16 + 50;
        if (hold)
            Assert.Equal(HybridHotkeyAction.Stop, state.Key(0xA0, false, endTime, bindings, audio.IsRecording));
        else
        {
            state.Key(0xA2, true, endTime, bindings, true);
            Assert.Equal(HybridHotkeyAction.Stop, state.Key(0xA0, true, endTime, bindings, true));
        }
        return audio.StopRecording() ?? [];
    }

    private sealed class ReplayDevice : IAudioInputDeviceProvider
    {
        public int DeviceCount => 1;
        public string GetDeviceName(int index) => "Synthetic replay";
        public string? GetDefaultDeviceName() => "Synthetic replay";
        public AudioInputDeviceInfo GetDeviceInfo(int index) => new(0, "replay", "Synthetic replay", true);
        public IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos() => [GetDeviceInfo(0)];
    }
    private sealed class ReplayInput : IAudioInputCaptureFactory, IAudioInputCapture
    {
        public bool Running;
        public bool CanRestartAfterStop => true;
        public WaveFormat WaveFormat => new(16000, 16, 1);
        public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
        public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
        public IAudioInputCapture Create(int device, WaveFormat format, int bufferMilliseconds) => this;
        public void Prepare() { }
        public void StartRecording() => Running = true;
        public void StopRecording() { Running = false; RecordingStopped?.Invoke(this, new()); }
        public void Dispose() => Running = false;
        public void Feed(short[] samples)
        {
            if (!Running) return;
            var data = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, data, 0, data.Length);
            DataAvailable?.Invoke(this, new(data, data.Length));
        }
    }
}
