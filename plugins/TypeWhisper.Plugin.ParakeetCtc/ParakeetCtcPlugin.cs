using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ParakeetCtc;

public sealed class ParakeetCtcPlugin : IVocabularyRescorerPlugin
{
    private NemoCtcModel? _model;
    private CtcTokenizer? _tokenizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private IPluginHostServices? _host;
    public string PluginId => "com.typewhisper.parakeet-ctc";
    public string PluginName => "Parakeet CTC Vocabulary (experimental)";
    public string PluginVersion => "0.1.0";
    public bool IsReady => !_disposed && _model is not null;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_model is not null) return;
            var directory = host.GetSetting<string>("ModelDirectory") ?? Path.Combine(host.PluginAssetDirectory, "model");
            var tokenizer = new CtcTokenizer(Path.Combine(directory, "tokens.txt"));
            var model = await Task.Run(() => new NemoCtcModel(Path.Combine(directory, "model.int8.onnx")));
            if (model.Metadata.GetValueOrDefault("subsampling_factor") != "8" ||
                model.Metadata.GetValueOrDefault("normalize_type") != "per_feature" || tokenizer.BlankId != 1024)
            { model.Dispose(); throw new NotSupportedException("Expected the Parakeet 110M CTC export and matching tokens."); }
            _tokenizer = tokenizer; _model = model; _host = host;
        }
        finally { _gate.Release(); }
        host.NotifyCapabilitiesChanged();
    }

    public async Task<VocabularyRescoreResult> RescoreAsync(VocabularyRescoreRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_model is null || _tokenizer is null) throw new InvalidOperationException("CTC model is not loaded.");
            if (request.SampleRate != 16000) throw new NotSupportedException("CTC requires 16 kHz mono PCM.");
            return await Task.Run(() => Rescore(request, cancellationToken), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private VocabularyRescoreResult Rescore(VocabularyRescoreRequest request, CancellationToken cancellation)
    {
        void Trace(string message) => _host?.Log(PluginLogLevel.Info, $"{request.RecordingId} {message}");
        VocabularyRescoreResult Skip(string reason) { Trace("plugin-skipped reason=" + reason); return new(request.RecordingId, []); }
        Trace("plugin-enter");
        if (request.Audio.Length < 400 || request.Terms.Count == 0 || request.TokenTimings.Count == 0) return Skip("missing-input");
        var duration = request.Audio.Length / 16000d;
        var aligned = new List<(int Start, int End, double From, double To)>();
        var cursor = 0;
        foreach (var timing in request.TokenTimings)
        {
            if (!double.IsFinite(timing.StartSeconds) || !double.IsFinite(timing.EndSeconds) || timing.StartSeconds < 0 ||
                timing.EndSeconds <= timing.StartSeconds || timing.EndSeconds > duration) return Skip($"invalid-timing index={aligned.Count} start={timing.StartSeconds:R} end={timing.EndSeconds:R} duration={duration:R}");
            var token = timing.Text.Replace('▁', ' ').Trim();
            if (token.Length == 0) continue;
            var position = request.Text.IndexOf(token, cursor, StringComparison.OrdinalIgnoreCase);
            if (position < 0) return Skip($"token-text-alignment index={aligned.Count} cursor={cursor}");
            aligned.Add((position, position + token.Length, timing.StartSeconds, timing.EndSeconds));
            cursor = position + token.Length;
        }
        var words = Regex.Matches(request.Text, @"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*").Cast<Match>().ToArray();
        var candidates = new List<(int Start, int Length, string Term, string Original, double From, double To)>();
        var similarityRejected = 0; var timingRejected = 0; double bestSimilarity = 0;
        foreach (var term in request.Terms.Take(256).DistinctBy(t => t.Text, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(term.Text) || term.Text.Length > 160) continue;
            var threshold = term.MinimumSimilarity ?? CtcBiasPolicy.MinimumSimilarity(Math.Min(request.Terms.Count, 256));
            if (!float.IsFinite(threshold) || threshold is < 0 or > 1) continue;
            for (var first = 0; first < words.Length; first++)
            for (var count = 1; count <= 3 && first + count <= words.Length; count++)
            {
                var start = words[first].Index;
                var end = words[first + count - 1].Index + words[first + count - 1].Length;
                var original = request.Text[start..end];
                var similarity = Similarity(original, term.Text);
                bestSimilarity = Math.Max(bestSimilarity, similarity);
                if (original == term.Text || similarity < threshold) { similarityRejected++; continue; }
                var times = aligned.Where(t => t.Start < end && t.End > start).ToArray();
                if (times.Length == 0 || times[0].Start > start || times[^1].End < end) { timingRejected++; continue; }
                candidates.Add((start, end - start, term.Text, original, times[0].From, times[^1].To));
            }
        }
        var proposals = new List<VocabularyReplacement>();
        Trace($"candidates count={candidates.Count} similarityRejected={similarityRejected} timingRejected={timingRejected} bestSimilarity={bestSimilarity:R} termLimit=256 candidateLimit=64");
        // Cache one bounded emission window at a time; long recordings do not
        // allocate an unbounded [time, vocabulary] matrix.
        int cachedStart = -1, cachedEnd = -1;
        CtcEmission? emission = null;
        foreach (var candidate in candidates.OrderBy(c => c.From).Take(64))
        {
            cancellation.ThrowIfCancellationRequested();
            var start = Math.Max(0, (int)((candidate.From - .5) * 16000));
            var end = Math.Min(request.Audio.Length, (int)Math.Ceiling((candidate.To + .5) * 16000));
            if (end - start is < 400 or > 16000 * 30) { Trace($"candidate-skipped start={candidate.Start} reason=audio-window"); continue; }
            if (start < cachedStart || end > cachedEnd || emission is null)
            {
                cachedStart = start;
                cachedEnd = Math.Min(request.Audio.Length, start + 16000 * 30);
                emission = _model!.Evaluate(request.Audio.Slice(cachedStart, cachedEnd - cachedStart), cancellation);
            }
            var from = (int)((start - cachedStart) / 16000d / emission.FrameSeconds);
            var to = (int)Math.Ceiling((end - cachedStart) / 16000d / emission.FrameSeconds);
            (double Score, int Tokens) Score(string value, bool variants)
            {
                var encodings = variants ? new[] { _tokenizer!.Encode(value), _tokenizer!.Encode(value, false) } : new[] { _tokenizer!.Encode(value) };
                return encodings.Select(tokens => (Score: CtcVocabularyScorer.Score(emission, tokens, _tokenizer!.BlankId, from, to, cancellation), Tokens: tokens.Length))
                    .OrderByDescending(result => result.Score).First();
            }
            var preferred = Score(candidate.Term, true); var original = Score(candidate.Original, false);
            var bonus = CtcBiasPolicy.Bonus(preferred.Tokens);
            var accepted = CtcBiasPolicy.Accept(original.Score, preferred.Score, preferred.Tokens);
            Trace($"candidate start={candidate.Start} length={candidate.Length} originalScore={original.Score:R} preferredScore={preferred.Score:R} tokens={preferred.Tokens} bonus={bonus:R} accepted={accepted}");
            if (accepted)
                proposals.Add(new(candidate.Start, candidate.Length, candidate.Term, preferred.Score + bonus - original.Score));
        }
        var selected = new List<VocabularyReplacement>();
        foreach (var proposal in proposals.OrderByDescending(p => p.Score))
            if (!selected.Any(p => p.Start < proposal.Start + proposal.Length && proposal.Start < p.Start + p.Length)) selected.Add(proposal);
        Trace($"plugin-finish replacements={selected.Count}");
        return new(request.RecordingId, selected.OrderBy(p => p.Start).ToArray());
    }

    private static double Similarity(string a, string b)
    {
        a = string.Concat(a.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        b = string.Concat(b.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0 || a.Length > 160) return 0;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            { var previous = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)); diagonal = previous; }
        }
        return 1 - row[^1] / (double)Math.Max(a.Length, b.Length);
    }

    public async Task DeactivateAsync()
    {
        await _gate.WaitAsync();
        try { _model?.Dispose(); _model = null; _tokenizer = null; }
        finally { _gate.Release(); }
    }
    public void Dispose() { _disposed = true; DeactivateAsync().GetAwaiter().GetResult(); }
}
