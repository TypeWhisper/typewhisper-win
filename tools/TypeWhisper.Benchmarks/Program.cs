using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.Json;
using NAudio.Wave;
using SherpaOnnx;

// Explicit opt-in local benchmark. Never starts dictation, changes settings,
// sends input to applications, downloads models or reads personal recordings.
if (args.Length != 2 || args[0] is not ("api" or "decoder"))
    throw new ArgumentException("Usage: api http://127.0.0.1:8978 OR decoder <existing-model-directory>");
var mode = args[0];
using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
JsonElement? status = null;
OfflineRecognizer? decoder = null;
var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
var startup = Stopwatch.StartNew();
if (mode == "api")
{
    var uri = new Uri(args[1]);
    if (!uri.IsLoopback || uri.Scheme != "http" || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo))
        throw new ArgumentException("Only a loopback HTTP base address is permitted.");
    client.BaseAddress = uri;
    var token = Environment.GetEnvironmentVariable("TYPEWHISPER_BENCHMARK_TOKEN");
    if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    status = await client.GetFromJsonAsync<JsonElement>("/v1/status");
    if (status.Value.GetProperty("status").GetString() != "ready" ||
        status.Value.GetProperty("engine").GetString() != "sherpa-onnx" ||
        status.Value.GetProperty("model").GetString() != "parakeet-tdt-0.6b" ||
        status.Value.GetProperty("acceleration").GetProperty("active_backend").GetString() != "cpu")
        throw new InvalidOperationException("Benchmark requires the existing local Parakeet TDT 0.6B CPU model already active.");
}
else
{
    var config = new OfflineRecognizerConfig();
    foreach (var name in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
        if (!File.Exists(Path.Combine(args[1], name))) throw new FileNotFoundException("Missing model file: " + name);
    config.ModelConfig.Transducer.Encoder = Path.Combine(args[1], "encoder.int8.onnx");
    config.ModelConfig.Transducer.Decoder = Path.Combine(args[1], "decoder.int8.onnx");
    config.ModelConfig.Transducer.Joiner = Path.Combine(args[1], "joiner.int8.onnx");
    config.ModelConfig.Tokens = Path.Combine(args[1], "tokens.txt");
    config.ModelConfig.NumThreads = threads;
    config.ModelConfig.Provider = "cpu";
    config.DecodingMethod = "greedy_search";
    decoder = new(config);
}
startup.Stop();
using var ownedDecoder = decoder;
var folder = Path.Combine(Path.GetTempPath(), "typewhisper-benchmark-" + Guid.NewGuid());
Directory.CreateDirectory(folder);
try
{
    using var synth = new SpeechSynthesizer();
    var voice = synth.GetInstalledVoices().First(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
    synth.SelectVoice(voice.VoiceInfo.Name);
    using var pcm = new MemoryStream();
    synth.SetOutputToAudioStream(pcm, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
    synth.Speak("Bananas are yellow. This is a test of immediate recording.");
    synth.SetOutputToNull();
    var unit = pcm.ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { mode, voice = voice.VoiceInfo.Name, processors = Environment.ProcessorCount,
        decoder_threads = mode == "decoder" ? (int?)threads : null, startup_ms = startup.Elapsed.TotalMilliseconds,
        startup_scope = mode == "decoder" ? "model construction" : "status request; app already running", status,
        scope = mode == "api" ? "legacy file API including conversion and postprocessing" : "WinUI-equivalent decoder configuration; NOT the WinUI app" }));
    foreach (var repeat in new[] { 1, 3, 6 })
    {
        var bytes = Enumerable.Range(0, repeat).SelectMany(_ => unit).ToArray();
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        var file = Path.Combine(folder, "fixture.wav");
        using (var writer = new WaveFileWriter(file, new WaveFormat(16000, 16, 1))) writer.Write(bytes, 0, bytes.Length);
        var timings = new List<double>();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var timer = Stopwatch.StartNew();
            string text;
            JsonElement? processing = null;
            if (mode == "api")
            {
                using var response = await client.PostAsJsonAsync("/v1/transcribe/local-file", new { path = file,
                    engine = "sherpa-onnx", model = "parakeet-tdt-0.6b", response_format = "json", await_download = false });
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Local API returned HTTP " + (int)response.StatusCode);
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                text = result.GetProperty("text").GetString() ?? "";
                if (result.TryGetProperty("processing_time", out var reported)) processing = reported.Clone();
                if (result.GetProperty("engine").GetString() != "sherpa-onnx" || result.GetProperty("model").GetString() != "parakeet-tdt-0.6b")
                    throw new InvalidOperationException("The API used a different engine/model.");
            }
            else
            {
                using var stream = decoder!.CreateStream();
                stream.AcceptWaveform(16000, samples); decoder.Decode(stream); text = stream.Result.Text.Trim();
            }
            timer.Stop();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Empty transcript: run is not a valid speed measurement.");
            if (iteration > 0) timings.Add(timer.Elapsed.TotalMilliseconds);
            Console.WriteLine(JsonSerializer.Serialize(new { repeat, iteration, warmup = iteration == 0,
                audio_seconds = samples.Length / 16000d, pcm_sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                elapsed_ms = timer.Elapsed.TotalMilliseconds, api_processing_time = processing, text }));
        }
        timings.Sort();
        Console.WriteLine(JsonSerializer.Serialize(new { repeat, summary = true, measured_runs = timings.Count,
            median_ms = timings[1], min_ms = timings[0], max_ms = timings[^1],
            realtime_factor = timings[1] / 1000 / (samples.Length / 16000d) }));
    }
}
finally { Directory.Delete(folder, true); }
