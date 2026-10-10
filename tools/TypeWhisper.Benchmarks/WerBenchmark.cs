using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SherpaOnnx;
using TypeWhisper.PluginSDK;

/// <summary>
/// Scores a sherpa-onnx transducer directory, or a transcribe.cpp GGUF file, on a pinned public FLEURS subset.
/// Downloads corpus audio only; model files must already exist.
/// </summary>
internal static class WerBenchmark
{
    private const int SampleRate = 16000;
    private const float NormalizationTarget = 0.707f;
    private const float RecorderMinimumPeak = 0.01f;
    // Same pinned corpus as eng/benchmark_cohere_quantizations.py.
    private const string DatasetRepository = "FluidInference/fleurs";
    private const string DatasetRevision = "8944693da251acbaf2f9686bddc4fedce8bd2edd";

    private static readonly string[] AllLanguages =
    [
        "bg_bg", "cs_cz", "da_dk", "de_de", "el_gr", "en_us", "es_419", "et_ee", "fi_fi", "fr_fr", "hr_hr", "hu_hu", "it_it",
        "lt_lt", "lv_lv", "mt_mt", "nl_nl", "pl_pl", "pt_br", "ro_ro", "ru_ru", "sk_sk", "sl_si", "sv_se", "uk_ua"
    ];

    internal const string Usage =
        "wer <existing-model-directory-or-gguf> <corpus-cache-directory> [--languages de_de,en_us] [--samples 100] " +
        "[--threads N] [--noise-snr dB] [--concat-seconds N] [--output results.json] [--sherpa-cuda runtime-dir] " + TranscribeCppDecoder.OptionsUsage;
    internal const string FilesUsage = "files <existing-model-directory-or-gguf> <wav-directory> [--threads N] [--raw] [--timings] [--output results.json] [--sherpa-cuda runtime-dir] " +
        TranscribeCppDecoder.OptionsUsage;

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Usage: " + Usage);
        var modelDirectory = Path.GetFullPath(args[1]);
        var cacheDirectory = Path.GetFullPath(args[2]);
        var languages = AllLanguages;
        var sampleCount = 100;
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        double? noiseSnr = null;
        var concatSeconds = 0;
        string? output = null;
        var native = new TranscribeCppDecoder.Options();
        string? sherpaCuda = null;
        for (var index = 3; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[index]);
            var value = args[index + 1];
            if (native.TryParse(args[index], value)) continue;
            switch (args[index])
            {
                case "--languages": languages = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--samples": sampleCount = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--threads": threads = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--noise-snr": noiseSnr = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--concat-seconds": concatSeconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--output": output = Path.GetFullPath(value); break;
                case "--sherpa-cuda": sherpaCuda = Path.GetFullPath(value); break;
                default: throw new ArgumentException("Unknown option: " + args[index]);
            }
        }
        if (languages.Except(AllLanguages).FirstOrDefault() is { } unknown)
            throw new ArgumentException("Unsupported FLEURS language: " + unknown);

        var load = Stopwatch.StartNew();
        using var decoder = CreateDecoder(modelDirectory, threads, native, sherpaCuda, out var modelFiles);
        load.Stop();
        var modelRoot = File.Exists(modelDirectory) ? Path.GetDirectoryName(modelDirectory)! : modelDirectory;

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var languageResults = new List<object>();
        var clipResults = new List<object>();
        var macroWer = new List<double>();
        long totalWordEdits = 0, totalWords = 0, totalCharacterEdits = 0, totalCharacters = 0;
        double totalAudioSeconds = 0, totalDecodeSeconds = 0;
        var warmedUp = false;

        foreach (var language in languages)
        {
            var clips = await PrepareClipsAsync(client, cacheDirectory, language, sampleCount);
            var units = concatSeconds > 0 ? Concatenate(clips, concatSeconds) : clips;
            long wordEdits = 0, words = 0, characterEdits = 0, characters = 0;
            double audioSeconds = 0, decodeSeconds = 0;
            var latencies = new List<double>();

            foreach (var unit in units)
            {
                var samples = unit.Samples;
                if (noiseSnr is { } snr) samples = AddNoise(samples, snr, unit.Id);
                if (!warmedUp)
                {
                    decoder.Decode(samples);
                    warmedUp = true;
                }

                var timer = Stopwatch.StartNew();
                var hypothesis = decoder.Decode(samples);
                timer.Stop();

                var reference = Normalize(unit.Reference);
                var normalizedHypothesis = Normalize(hypothesis);
                var referenceWords = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var clipWordEdits = EditDistance(referenceWords, normalizedHypothesis.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var referenceCharacters = reference.Replace(" ", "").EnumerateRunes().ToArray();
                var clipCharacterEdits = EditDistance(referenceCharacters, normalizedHypothesis.Replace(" ", "").EnumerateRunes().ToArray());
                var seconds = samples.Length / (double)SampleRate;

                wordEdits += clipWordEdits; words += referenceWords.Length;
                characterEdits += clipCharacterEdits; characters += referenceCharacters.Length;
                audioSeconds += seconds; decodeSeconds += timer.Elapsed.TotalSeconds;
                latencies.Add(timer.Elapsed.TotalMilliseconds);
                clipResults.Add(new { language, id = unit.Id, audio_seconds = seconds, elapsed_ms = timer.Elapsed.TotalMilliseconds,
                    word_edits = clipWordEdits, reference_words = referenceWords.Length, hypothesis });
            }

            latencies.Sort();
            var wer = wordEdits / (double)words;
            macroWer.Add(wer);
            totalWordEdits += wordEdits; totalWords += words; totalCharacterEdits += characterEdits; totalCharacters += characters;
            totalAudioSeconds += audioSeconds; totalDecodeSeconds += decodeSeconds;
            var row = new { language, units = units.Count, audio_seconds = audioSeconds, wer, cer = characterEdits / (double)characters,
                realtime_factor = decodeSeconds / audioSeconds, median_ms = latencies[latencies.Count / 2] };
            languageResults.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }

        using var process = Process.GetCurrentProcess();
        var summary = new
        {
            model_directory = modelDirectory,
            engine = decoder.Description,
            model_files = modelFiles.Select(path => new { name = Path.GetRelativePath(modelRoot, path).Replace('\\', '/'), bytes = new FileInfo(path).Length,
                sha256 = Sha256(path) }),
            dataset = DatasetRepository + "@" + DatasetRevision,
            samples_per_language = sampleCount,
            noise_snr_db = noiseSnr,
            concat_seconds = concatSeconds,
            threads,
            processors = Environment.ProcessorCount,
            load_ms = load.Elapsed.TotalMilliseconds,
            peak_working_set_mb = process.PeakWorkingSet64 / (1024d * 1024),
            languages = languages.Length,
            macro_wer = macroWer.Average(),
            micro_wer = totalWordEdits / (double)totalWords,
            micro_cer = totalCharacterEdits / (double)totalCharacters,
            audio_seconds = totalAudioSeconds,
            realtime_factor = totalDecodeSeconds / totalAudioSeconds
        };
        Console.WriteLine(JsonSerializer.Serialize(summary));
        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { summary, languages = languageResults, clips = clipResults },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    // A .gguf file runs through transcribe.cpp; a directory is scored with sherpa-onnx as before.
    private static IBenchmarkDecoder CreateDecoder(string modelPath, int threads, TranscribeCppDecoder.Options native, string? sherpaCuda,
        out string[] modelFiles)
    {
        if (modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            if (sherpaCuda is not null) throw new ArgumentException("--sherpa-cuda applies to sherpa-onnx models only.");
            if (!File.Exists(modelPath)) throw new FileNotFoundException("Missing model file: " + modelPath);
            modelFiles = [modelPath];
            return new TranscribeCppDecoder(modelPath, threads, native);
        }
        native.EnsureUnused();
        var config = CreateConfig(modelPath, threads, out var qwen, out modelFiles);
        if (sherpaCuda is not null)
        {
            UseSherpaRuntime(sherpaCuda);
            config.ModelConfig.Provider = "cuda";
        }
        return new SherpaDecoder(new OfflineRecognizer(config), qwen, config.ModelConfig.Provider, threads);
    }

    private static OfflineRecognizerConfig CreateConfig(string modelDirectory, int threads, out bool qwen, out string[] modelFiles)
    {
        // A Qwen3-ASR directory is recognized by its conv frontend; everything else is scored as a transducer.
        qwen = File.Exists(Path.Join(modelDirectory, "conv_frontend.onnx"));
        var config = new OfflineRecognizerConfig();
        if (qwen)
        {
            // Mirrors QwenRecognizer in the Qwen3 ASR (Local) plugin.
            modelFiles = new[] { "conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx", "tokenizer/vocab.json",
                "tokenizer/merges.txt", "tokenizer/tokenizer_config.json" }.Select(name => Path.Join(modelDirectory, name)).ToArray();
            if (modelFiles.FirstOrDefault(path => !File.Exists(path)) is { } missing)
                throw new FileNotFoundException("Missing model file: " + Path.GetRelativePath(modelDirectory, missing));
            config.ModelConfig.Qwen3Asr.ConvFrontend = modelFiles[0];
            config.ModelConfig.Qwen3Asr.Encoder = modelFiles[1];
            config.ModelConfig.Qwen3Asr.Decoder = modelFiles[2];
            config.ModelConfig.Qwen3Asr.Tokenizer = Path.Join(modelDirectory, "tokenizer");
            config.ModelConfig.Qwen3Asr.MaxTotalLen = 512;
            config.ModelConfig.Qwen3Asr.MaxNewTokens = 256;
            config.ModelConfig.Tokens = "";
        }
        else
        {
            var files = new[] { "encoder", "decoder", "joiner" }.ToDictionary(name => name, name => ResolveModelFile(modelDirectory, name));
            var tokens = Path.Join(modelDirectory, "tokens.txt");
            if (!File.Exists(tokens)) throw new FileNotFoundException("Missing model file: tokens.txt");
            modelFiles = files.Values.Append(tokens).ToArray();

            // Mirrors SherpaOnnxPlugin.CreateParakeetConfig apart from the resolved file names.
            config.ModelConfig.Transducer.Encoder = files["encoder"];
            config.ModelConfig.Transducer.Decoder = files["decoder"];
            config.ModelConfig.Transducer.Joiner = files["joiner"];
            config.ModelConfig.Tokens = tokens;
            config.DecodingMethod = "greedy_search";
        }
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        return config;
    }

    /// <summary>
    /// Transcribes every WAV in a directory. A UTF-8 <c>name.txt</c> next to <c>name.wav</c> holds what was said and is
    /// scored for WER and CER after normalization. An optional <c>name.formatted.txt</c> holds the intended final text;
    /// the exact flag compares against it (or against <c>name.txt</c>) including punctuation and casing.
    /// </summary>
    internal static async Task FilesAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Usage: " + FilesUsage);
        var modelDirectory = Path.GetFullPath(args[1]);
        var wavDirectory = Path.GetFullPath(args[2]);
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        string? output = null;
        var normalize = true;
        var timings = false;
        var native = new TranscribeCppDecoder.Options();
        string? sherpaCuda = null;
        for (var index = 3; index < args.Length; index += 2)
        {
            // --raw skips the recorder's peak normalization, like file transcription of an unprocessed WAV.
            if (args[index] == "--raw") { normalize = false; index--; continue; }
            // --timings records token times for an offline CTC vocabulary rescoring pass (tests/TypeWhisper.ParakeetCtc.Probe).
            if (args[index] == "--timings") { timings = true; index--; continue; }
            if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[index]);
            if (native.TryParse(args[index], args[index + 1])) continue;
            switch (args[index])
            {
                case "--threads": threads = int.Parse(args[index + 1], CultureInfo.InvariantCulture); break;
                case "--output": output = Path.GetFullPath(args[index + 1]); break;
                case "--sherpa-cuda": sherpaCuda = Path.GetFullPath(args[index + 1]); break;
                default: throw new ArgumentException("Unknown option: " + args[index]);
            }
        }
        var load = Stopwatch.StartNew();
        using var decoder = CreateDecoder(modelDirectory, threads, native, sherpaCuda, out var modelFiles);
        load.Stop();
        var modelRoot = File.Exists(modelDirectory) ? Path.GetDirectoryName(modelDirectory)! : modelDirectory;
        var paths = Directory.GetFiles(wavDirectory, "*.wav").Order(StringComparer.Ordinal).Where(HasAudio).ToArray();
        if (paths.Length == 0) throw new FileNotFoundException("No WAV files in " + wavDirectory);
        // Each recording is read when it is scored, so long directories never hold more than one decoded file.
        static Clip Load(string path, bool normalize) => new(Path.GetFileNameWithoutExtension(path), ReadOptional(Path.ChangeExtension(path, ".txt")),
            ReadWav(path, normalize, RecorderMinimumPeak), ReadOptional(Path.ChangeExtension(path, ".formatted.txt")));
        decoder.Decode(Load(paths[0], normalize).Samples);
        var results = new List<object>();
        double audioSeconds = 0, decodeSeconds = 0;
        long wordEdits = 0, words = 0, characterEdits = 0, characters = 0;
        int scored = 0, exact = 0;
        foreach (var clip in paths.Select(path => Load(path, normalize)))
        {
            var timer = Stopwatch.StartNew();
            var (hypothesis, tokenTimings) = timings ? decoder.DecodeWithTimings(clip.Samples) : (decoder.Decode(clip.Samples), []);
            timer.Stop();
            var seconds = clip.Samples.Length / (double)SampleRate;
            audioSeconds += seconds; decodeSeconds += timer.Elapsed.TotalSeconds;
            int? clipWordEdits = null, referenceWords = null;
            bool? exactMatch = null;
            if (clip.Reference.Length > 0)
            {
                var reference = Normalize(clip.Reference);
                var normalized = Normalize(hypothesis);
                var referenceTokens = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                clipWordEdits = EditDistance(referenceTokens, normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                referenceWords = referenceTokens.Length;
                var referenceRunes = reference.Replace(" ", "").EnumerateRunes().ToArray();
                characterEdits += EditDistance(referenceRunes, normalized.Replace(" ", "").EnumerateRunes().ToArray());
                characters += referenceRunes.Length;
                wordEdits += clipWordEdits.Value; words += referenceTokens.Length;
                var target = clip.Formatted.Length > 0 ? clip.Formatted : clip.Reference;
                exactMatch = string.Equals(target.Normalize(NormalizationForm.FormKC), hypothesis.Normalize(NormalizationForm.FormKC), StringComparison.Ordinal);
                scored++; if (exactMatch == true) exact++;
            }
            var row = new { id = clip.Id, audio_seconds = seconds, elapsed_ms = timer.Elapsed.TotalMilliseconds, hypothesis,
                reference = clip.Reference.Length > 0 ? clip.Reference : null, formatted = clip.Formatted.Length > 0 ? clip.Formatted : null, word_edits = clipWordEdits, reference_words = referenceWords, exact = exactMatch,
                token_timings = timings ? tokenTimings.Select(t => new { text = t.Text, start = t.StartSeconds, end = t.EndSeconds }) : null };
            results.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        using var process = Process.GetCurrentProcess();
        var summary = new
        {
            model_directory = modelDirectory,
            engine = decoder.Description,
            model_files = modelFiles.Select(path => new { name = Path.GetRelativePath(modelRoot, path).Replace('\\', '/'), bytes = new FileInfo(path).Length }),
            threads, load_ms = load.Elapsed.TotalMilliseconds, peak_working_set_mb = process.PeakWorkingSet64 / (1024d * 1024),
            audio_seconds = audioSeconds, realtime_factor = decodeSeconds / audioSeconds,
            scored_clips = scored, exact_matches = exact,
            micro_wer = words > 0 ? wordEdits / (double)words : (double?)null,
            micro_cer = characters > 0 ? characterEdits / (double)characters : (double?)null
        };
        Console.WriteLine(JsonSerializer.Serialize(summary));
        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { summary, clips = results },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    private static string ResolveModelFile(string directory, string name)
    {
        foreach (var candidate in new[] { name + ".int8.onnx", name + ".onnx" })
            if (File.Exists(Path.Combine(directory, candidate))) return Path.Combine(directory, candidate);
        throw new FileNotFoundException("Missing model file: " + name + "[.int8].onnx");
    }

    // Loads sherpa-onnx from a directory holding the lib folder of a sherpa-onnx CUDA release plus the cuDNN and cuBLAS
    // DLLs it needs, to compare CUDA with the Vulkan path. It must match the org.k2fsa.sherpa.onnx package version.
    private static void UseSherpaRuntime(string directory)
    {
        var library = Path.Join(directory, "sherpa-onnx-c-api.dll");
        if (!File.Exists(library)) throw new FileNotFoundException("Missing sherpa-onnx CUDA runtime: " + library);
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, (name, _, _) =>
            name.StartsWith("sherpa-onnx-c-api", StringComparison.OrdinalIgnoreCase)
                ? System.Runtime.InteropServices.NativeLibrary.Load(library) : IntPtr.Zero);
    }

    private sealed class SherpaDecoder(OfflineRecognizer recognizer, bool qwen, string provider, int threads) : IBenchmarkDecoder
    {
        public string Description => $"sherpa-onnx {provider}, {threads} threads";
        public string Decode(float[] samples) => WerBenchmark.Decode(recognizer, samples, qwen);

        // Same conversion as SherpaOnnxPlugin for a single-chunk Parakeet recording.
        public (string Text, VocabularyTokenTiming[] Timings) DecodeWithTimings(float[] samples)
        {
            if (qwen) return (Decode(samples), []);
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            recognizer.Decode(stream);
            var result = stream.Result;
            return (result.Text.Trim(), result.Tokens is not null && result.Timestamps is not null
                ? TypeWhisper.PluginSDK.Helpers.TranscriptionTokenTimings.Create(result.Tokens, result.Timestamps, result.Durations, samples.Length / (double)SampleRate) : []);
        }
        public void Dispose() => recognizer.Dispose();
    }

    private static string Decode(OfflineRecognizer recognizer, float[] samples, bool qwen)
    {
        if (!qwen) return Decode(recognizer, samples);
        // The Qwen plugin decodes bounded windows of at most ten seconds and joins their text.
        var parts = new List<string>();
        for (var offset = 0; offset < samples.Length;)
        {
            var length = QwenChunkLength(samples.AsSpan(offset));
            // Same marker cleanup as QwenRecognizer.StripLanguageMarkers.
            var text = System.Text.RegularExpressions.Regex.Replace(Decode(recognizer, samples[offset..(offset + length)]),
                @"(?:language\s+[A-Za-z]+(?:\s[A-Za-z]+)?\s*)?<asr_text>", "").Trim();
            if (text.Length > 0) parts.Add(text);
            offset += length;
        }
        return string.Join(' ', parts);
    }

    private static string Decode(OfflineRecognizer recognizer, float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    // Same rule as QwenAudio.ChunkLength in the Qwen3 ASR (Local) plugin.
    private static int QwenChunkLength(ReadOnlySpan<float> remaining)
    {
        const int chunk = 10 * SampleRate;
        if (remaining.Length <= chunk) return remaining.Length;
        const int window = SampleRate / 50;
        var best = chunk;
        var minimum = double.MaxValue;
        for (var start = chunk - 3 * SampleRate; start + window <= chunk; start += window)
        {
            double energy = 0;
            foreach (var sample in remaining.Slice(start, window)) energy += sample * sample;
            if (energy <= minimum) { minimum = energy; best = start + window / 2; }
        }
        return best;
    }

    private static async Task<List<Clip>> PrepareClipsAsync(HttpClient client, string cacheDirectory, string language, int sampleCount)
    {
        var directory = Path.Combine(cacheDirectory, DatasetRepository.Replace('/', '-'), DatasetRevision, language);
        Directory.CreateDirectory(directory);
        var baseUrl = $"https://huggingface.co/datasets/{DatasetRepository}/resolve/{DatasetRevision}/{language}/";
        var transcripts = Path.Combine(directory, language + ".trans.txt");
        await DownloadAsync(client, baseUrl + language + ".trans.txt", transcripts);

        var entries = (await File.ReadAllLinesAsync(transcripts))
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2 && parts[1].Trim().Length > 0)
            .Take(sampleCount)
            .ToList();
        if (entries.Count < sampleCount)
            throw new InvalidOperationException($"{language} has only {entries.Count} transcript entries; {sampleCount} requested.");

        await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (entry, _) =>
            await DownloadAsync(client, baseUrl + entry[0] + ".wav", Path.Combine(directory, entry[0] + ".wav")));

        return entries.Select(entry => new Clip(entry[0], entry[1].Trim(), ReadWav(Path.Combine(directory, entry[0] + ".wav")))).ToList();
    }

    private static async Task DownloadAsync(HttpClient client, string url, string destination)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 0) return;
        var temporary = destination + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var source = await client.GetStreamAsync(url))
                await using (var target = File.Create(temporary))
                    await source.CopyToAsync(target);
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    private static bool HasAudio(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.Length > 0) return true;
        Console.Error.WriteLine("Skipping recording without audio: " + path);
        return false;
    }

    private static float[] ReadWav(string path, bool normalize = true, float minimumPeak = 0)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat is not { SampleRate: SampleRate, Channels: 1, BitsPerSample: 16, Encoding: WaveFormatEncoding.Pcm })
            throw new InvalidDataException("Expected 16 kHz mono 16-bit PCM: " + path);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;

        // AudioRecordingService raises quiet recordings to this peak before transcription and leaves near-silence
        // below 0.01 unscaled. FLEURS has clips far below that threshold that decode to nothing unscaled, so the
        // corpus path raises every clip; own recordings pass the recorder's threshold instead.
        var peak = samples.Max(MathF.Abs);
        if (normalize && peak > 0 && peak >= minimumPeak && peak < NormalizationTarget)
            for (var i = 0; i < samples.Length; i++) samples[i] *= NormalizationTarget / peak;
        return samples;
    }

    /// <summary>Joins consecutive clips into single passes of at most the given length.</summary>
    private static List<Clip> Concatenate(List<Clip> clips, int seconds)
    {
        var limit = seconds * SampleRate;
        var units = new List<Clip>();
        var group = new List<Clip>();
        var length = 0;
        foreach (var clip in clips)
        {
            if (group.Count > 0 && length + clip.Samples.Length > limit)
            {
                units.Add(Join(group));
                group.Clear();
                length = 0;
            }
            group.Add(clip);
            length += clip.Samples.Length;
        }
        if (group.Count > 0) units.Add(Join(group));
        return units;

        static Clip Join(List<Clip> group) => new(
            group[0].Id + "+" + (group.Count - 1),
            string.Join(' ', group.Select(clip => clip.Reference)),
            group.SelectMany(clip => clip.Samples).ToArray());
    }

    /// <summary>Adds white noise at the given signal-to-noise ratio, seeded per clip so every model hears the same audio.</summary>
    private static float[] AddNoise(float[] samples, double snrDb, string id)
    {
        var power = samples.Sum(sample => (double)sample * sample) / samples.Length;
        // Uniform noise in [-a, a] has power a²/3.
        var amplitude = Math.Sqrt(3 * power / Math.Pow(10, snrDb / 10));
        var random = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(id))));
        var noisy = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            noisy[i] = Math.Clamp(samples[i] + (float)((random.NextDouble() * 2 - 1) * amplitude), -1f, 1f);
        return noisy;
    }

    // Same rule as normalize_text in eng/benchmark_cohere_quantizations.py.
    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var separator = Rune.IsWhiteSpace(rune)
                || category is >= UnicodeCategory.ConnectorPunctuation and <= UnicodeCategory.OtherPunctuation
                || category is >= UnicodeCategory.MathSymbol and <= UnicodeCategory.OtherSymbol;
            if (!separator) builder.Append(rune.ToString());
            else if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }
        return builder.ToString().Trim();
    }

    private static int EditDistance<T>(T[] reference, T[] hypothesis) where T : IEquatable<T>
    {
        var previous = Enumerable.Range(0, hypothesis.Length + 1).ToArray();
        for (var row = 1; row <= reference.Length; row++)
        {
            var current = new int[hypothesis.Length + 1];
            current[0] = row;
            for (var column = 1; column <= hypothesis.Length; column++)
                current[column] = Math.Min(
                    Math.Min(current[column - 1], previous[column]) + 1,
                    previous[column - 1] + (reference[row - 1].Equals(hypothesis[column - 1]) ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : "";

    private sealed record Clip(string Id, string Reference, float[] Samples, string Formatted = "");
}
