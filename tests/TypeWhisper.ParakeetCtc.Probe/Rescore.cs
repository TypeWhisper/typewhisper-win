using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Moq;
using NAudio.Wave;
using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

/// <summary>
/// Applies the app's CTC vocabulary rescoring to a `files --timings` result of tools/TypeWhisper.Benchmarks, so engines
/// can be compared with dictionary boosting. It runs in its own process because the benchmark's sherpa-onnx runtime and
/// the CTC plugin's ONNX Runtime both ship onnxruntime.dll.
/// </summary>
internal static class Rescore
{
    internal const string Usage = "rescore <CTC model directory> <benchmark results.json> <wav directory> <terms file, one per line> [--output results.json]";

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length is not (5 or 7) || (args.Length == 7 && args[5] != "--output")) throw new ArgumentException("Usage: " + Usage);
        var terms = File.ReadAllLines(args[4]).Select(line => line.Trim()).Where(line => line.Length > 0)
            .Select(line => new VocabularyTermHint(line)).ToArray();
        using var results = JsonDocument.Parse(File.ReadAllText(args[2]));
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.GetSetting<string>("ModelDirectory")).Returns(args[1]);
        using var plugin = new ParakeetCtcPlugin();
        await plugin.ActivateAsync(host.Object);
        var pipeline = new VocabularyPipeline();

        long wordsBefore = 0, wordsAfter = 0, referenceWords = 0;
        int changed = 0, unaligned = 0;
        var elapsed = new List<double>();
        var rows = new List<object>();
        foreach (var clip in results.RootElement.GetProperty("clips").EnumerateArray())
        {
            var id = clip.GetProperty("id").GetString()!;
            var hypothesis = clip.GetProperty("hypothesis").GetString()!;
            var reference = clip.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
            if (!clip.TryGetProperty("token_timings", out var tokens) || tokens.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Run the benchmark with --timings.");
            var timings = tokens.EnumerateArray().Select(t => new VocabularyTokenTiming(t.GetProperty("text").GetString()!,
                t.GetProperty("start").GetDouble(), t.GetProperty("end").GetDouble())).ToArray();
            // The benchmark must have decoded the same samples; --raw reads the file unscaled, as here.
            var samples = ReadWav(Path.Join(args[3], id + ".wav"));
            var timer = Stopwatch.StartNew();
            var outcome = await pipeline.RefineAsync(plugin, Guid.NewGuid(), hypothesis, samples, 16000, timings, terms);
            timer.Stop();
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
            if (outcome.Error is not null) throw new InvalidOperationException(outcome.Error);
            if (outcome.Modified) changed++;
            if (timings.Length == 0) unaligned++;
            // Like the benchmark, only clips with a reference text count towards WER.
            int? before = null, after = null;
            if (reference.Length > 0)
            {
                var referenceTokens = Words(reference);
                before = EditDistance(referenceTokens, Words(hypothesis));
                after = EditDistance(referenceTokens, Words(outcome.Text));
                wordsBefore += before.Value; wordsAfter += after.Value; referenceWords += referenceTokens.Length;
            }
            var row = new { id, before = hypothesis, after = outcome.Text, word_edits_before = before, word_edits_after = after, ctc_ms = timer.Elapsed.TotalMilliseconds };
            rows.Add(row);
            if (outcome.Modified) Console.WriteLine(JsonSerializer.Serialize(row, Json));
        }
        elapsed.Sort();
        var summary = new
        {
            engine = results.RootElement.GetProperty("summary").GetProperty("engine").GetString(),
            terms = terms.Select(t => t.Text),
            clips = rows.Count, changed_clips = changed, clips_without_timings = unaligned,
            micro_wer_before = referenceWords > 0 ? wordsBefore / (double)referenceWords : (double?)null,
            micro_wer_after = referenceWords > 0 ? wordsAfter / (double)referenceWords : (double?)null,
            median_ctc_ms = elapsed[elapsed.Count / 2], max_ctc_ms = elapsed[^1]
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, Json));
        if (args.Length == 7) File.WriteAllText(args[6], JsonSerializer.Serialize(new { summary, clips = rows }, new JsonSerializerOptions(Json) { WriteIndented = true }));
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static float[] ReadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat is not { SampleRate: 16000, Channels: 1, BitsPerSample: 16 }) throw new InvalidDataException("Expected 16 kHz mono PCM16: " + path);
        var bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
        return Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
    }

    // Same normalization as WerBenchmark.Normalize in tools/TypeWhisper.Benchmarks.
    private static string[] Words(string text)
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
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static int EditDistance(string[] reference, string[] hypothesis)
    {
        var previous = Enumerable.Range(0, hypothesis.Length + 1).ToArray();
        for (var row = 1; row <= reference.Length; row++)
        {
            var current = new int[hypothesis.Length + 1];
            current[0] = row;
            for (var column = 1; column <= hypothesis.Length; column++)
                current[column] = Math.Min(Math.Min(current[column - 1], previous[column]) + 1,
                    previous[column - 1] + (reference[row - 1] == hypothesis[column - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }
}
