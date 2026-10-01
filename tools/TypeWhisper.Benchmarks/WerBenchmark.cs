using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SherpaOnnx;

/// <summary>
/// Scores a sherpa-onnx transducer directory on a pinned public FLEURS subset.
/// Downloads corpus audio only; model files must already exist.
/// </summary>
internal static class WerBenchmark
{
    private const int SampleRate = 16000;
    private const float NormalizationTarget = 0.707f;
    // Same pinned corpus as eng/benchmark_cohere_quantizations.py.
    private const string DatasetRepository = "FluidInference/fleurs";
    private const string DatasetRevision = "8944693da251acbaf2f9686bddc4fedce8bd2edd";

    private static readonly string[] AllLanguages =
    [
        "bg_bg", "cs_cz", "da_dk", "de_de", "el_gr", "en_us", "es_419", "et_ee", "fi_fi", "fr_fr", "hr_hr", "hu_hu", "it_it",
        "lt_lt", "lv_lv", "mt_mt", "nl_nl", "pl_pl", "pt_br", "ro_ro", "ru_ru", "sk_sk", "sl_si", "sv_se", "uk_ua"
    ];

    internal const string Usage =
        "wer <existing-model-directory> <corpus-cache-directory> [--languages de_de,en_us] [--samples 100] " +
        "[--threads N] [--noise-snr dB] [--concat-seconds N] [--output results.json]";

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
        for (var index = 3; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[index]);
            var value = args[index + 1];
            switch (args[index])
            {
                case "--languages": languages = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--samples": sampleCount = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--threads": threads = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--noise-snr": noiseSnr = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--concat-seconds": concatSeconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--output": output = Path.GetFullPath(value); break;
                default: throw new ArgumentException("Unknown option: " + args[index]);
            }
        }
        if (languages.Except(AllLanguages).FirstOrDefault() is { } unknown)
            throw new ArgumentException("Unsupported FLEURS language: " + unknown);

        var files = new[] { "encoder", "decoder", "joiner" }.ToDictionary(name => name, name => ResolveModelFile(modelDirectory, name));
        var tokens = Path.Combine(modelDirectory, "tokens.txt");
        if (!File.Exists(tokens)) throw new FileNotFoundException("Missing model file: tokens.txt");

        // Mirrors SherpaOnnxPlugin.CreateParakeetConfig apart from the resolved file names.
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = files["encoder"];
        config.ModelConfig.Transducer.Decoder = files["decoder"];
        config.ModelConfig.Transducer.Joiner = files["joiner"];
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        var load = Stopwatch.StartNew();
        using var recognizer = new OfflineRecognizer(config);
        load.Stop();

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
                    Decode(recognizer, samples);
                    warmedUp = true;
                }

                var timer = Stopwatch.StartNew();
                var hypothesis = Decode(recognizer, samples);
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
            model_files = files.Values.Append(tokens).Select(path => new { name = Path.GetFileName(path), bytes = new FileInfo(path).Length,
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

    private static string ResolveModelFile(string directory, string name)
    {
        foreach (var candidate in new[] { name + ".int8.onnx", name + ".onnx" })
            if (File.Exists(Path.Combine(directory, candidate))) return Path.Combine(directory, candidate);
        throw new FileNotFoundException("Missing model file: " + name + "[.int8].onnx");
    }

    private static string Decode(OfflineRecognizer recognizer, float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
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

    private static float[] ReadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat is not { SampleRate: SampleRate, Channels: 1, BitsPerSample: 16, Encoding: WaveFormatEncoding.Pcm })
            throw new InvalidDataException("Expected 16 kHz mono 16-bit PCM: " + path);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;

        // AudioRecordingService raises quiet recordings to this peak before transcription. FLEURS has clips
        // far below its 0.01 threshold that decode to nothing unscaled, so every clip is raised here.
        var peak = samples.Max(MathF.Abs);
        if (peak > 0 && peak < NormalizationTarget)
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

    private sealed record Clip(string Id, string Reference, float[] Samples);
}
