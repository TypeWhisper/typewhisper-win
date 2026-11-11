using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TypeWhisper.DiarizationBenchmarks.Workers;

/// <summary>Streaming profile in 80 ms encoder frames, as NVIDIA's model card and FluidAudio define them.</summary>
internal sealed record NemotronProfile(int ChunkLength, int RightContext, int FifoLength, int SpeakerCacheLength, int UpdatePeriod)
{
    /// <summary>FluidAudio's fast32 preset: 2.88 s input buffer.</summary>
    public static readonly NemotronProfile Fast32 = new(32, 4, 40, 264, 40);

    /// <summary>The model card's "very high latency (offline)" profile: 30.4 s input buffer.</summary>
    public static readonly NemotronProfile Offline = new(340, 40, 40, 264, 300);
}

/// <summary>
/// Runs onnx-community/Nemotron-3-Diarization-ONNX chunk by chunk. The ONNX graph is one stateless step
/// (chunk features + cached embeddings in, 10 ms logits for the whole sequence + the chunk's pre-encoded
/// embeddings + the learned silence embedding out); the Arrival-Order Speaker Cache and FIFO queue live
/// here. This is a port of Transformers' Nemotron3DiarizationSpeakerCache and of the offline chunk loop of
/// Nemotron3DiarizationForAudioFrameClassification.forward, for batch size 1 and no left context.
/// </summary>
internal sealed class NemotronStreamingDiarizer(InferenceSession session, NemotronProfile profile, bool fixedShapes = false)
{
    /// <summary>Largest speaker cache plus FIFO, in encoder frames: the cached input with fixed shapes.</summary>
    public int CachedCapacity => profile.SpeakerCacheLength + profile.FifoLength;

    /// <summary>Chunk plus look-ahead, in encoder frames: the chunk input with fixed shapes.</summary>
    public int StepCapacity => profile.ChunkLength + profile.RightContext;

    private const int Hidden = 512;
    private const int Speakers = 8;
    private const int Subsampling = 8;
    private const int SilenceFramesPerSpeaker = 1;
    private const float PredictionScoreThreshold = 0.25f;
    private const float LatestFramesScoreBoost = 0.05f;
    private const float MinPositiveScoresRate = 0.5f;
    private const float StrongBoostRate = 0.75f;
    private const float WeakBoostRate = 1.5f;
    private static readonly float LogHalf = MathF.Log(0.5f);

    private readonly int _minPositiveScores = (int)Math.Floor(Budget(profile) * MinPositiveScoresRate);
    private readonly int _strongBoosted = (int)Math.Floor(Budget(profile) * StrongBoostRate);
    private readonly int _weakBoosted = (int)Math.Floor(Budget(profile) * WeakBoostRate);

    private static readonly bool Trace = Environment.GetEnvironmentVariable("TYPEWHISPER_DIARIZATION_TRACE") == "1";

    private static int Budget(NemotronProfile p) => (p.SpeakerCacheLength / Speakers) - SilenceFramesPerSpeaker;

    /// <summary>Speaker probabilities, [frames × 8] row-major at 10 ms.</summary>
    public float[] Run(float[] features, int frames, int validFrames)
    {
        var embedCount = (frames + Subsampling - 1) / Subsampling;
        var embedMask = new long[embedCount];
        for (var i = 0; i < embedCount; i++)
        {
            embedMask[i] = i * Subsampling < validFrames ? 1 : 0;
        }

        // Held for the whole run: a RunOptions created inline in the Run call can be finalized while
        // the native call still uses it, which crashed long files with an access violation.
        using var runOptions = new RunOptions();
        var state = new State(profile);
        float[]? silence = null;
        var output = new float[embedCount * Subsampling * Speakers];
        var written = 0;
        for (var start = 0; start < embedCount; start += profile.ChunkLength)
        {
            var end = Math.Min(start + profile.ChunkLength, embedCount);
            var contextEnd = Math.Min(end + profile.RightContext, embedCount);
            var melStart = start * Subsampling;
            var melEnd = Math.Min(contextEnd * Subsampling, frames);
            var chunkFrames = end - start;
            var stepFrames = contextEnd - start;

            var cachedLength = state.CacheCount + state.FifoCount;
            var mask = new long[cachedLength + stepFrames];
            Array.Fill(mask, 1L, 0, cachedLength);
            Array.Copy(embedMask, start, mask, cachedLength, stepFrames);

            // With fixed shapes the cached frames are padded on the left and the chunk on the right, both
            // masked out. Attention is masked and RoPE only sees relative positions, so the valid frames
            // get the same result; the zero mel frames after the end match the encoder's own padding.
            var padCached = fixedShapes ? CachedCapacity - cachedLength : 0;
            var padStep = fixedShapes ? StepCapacity - stepFrames : 0;
            var inputCached = cachedLength + padCached;
            var inputStep = stepFrames + padStep;

            var cached = new float[inputCached * Hidden];
            Array.Copy(state.CacheEmbeds, 0, cached, padCached * Hidden, state.CacheCount * Hidden);
            Array.Copy(state.FifoEmbeds, 0, cached, (padCached + state.CacheCount) * Hidden, state.FifoCount * Hidden);

            var inputMask = mask;
            if (fixedShapes)
            {
                inputMask = new long[inputCached + inputStep];
                Array.Copy(mask, 0, inputMask, padCached, mask.Length);
            }

            var melFrames = fixedShapes ? inputStep * Subsampling : melEnd - melStart;
            var chunkFeatures = new float[melFrames * MelFrontend.Mels];
            Array.Copy(features, melStart * MelFrontend.Mels, chunkFeatures, 0, (melEnd - melStart) * MelFrontend.Mels);

            using var inputFeatures = OrtValue.CreateTensorValueFromMemory(chunkFeatures, [1, melFrames, MelFrontend.Mels]);
            using var cachedEmbeds = OrtValue.CreateTensorValueFromMemory(cached, [1, inputCached, Hidden]);
            using var attentionMask = OrtValue.CreateTensorValueFromMemory(inputMask, [1, inputMask.Length]);
            var stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            using var results = session.Run(
                runOptions,
                ["input_features", "cached_embeds", "attention_mask"],
                [inputFeatures, cachedEmbeds, attentionMask],
                ["logits", "chunk_embeds", "silence_embeds"]);

            if (Trace)
            {
                Console.Error.WriteLine($"step {start / profile.ChunkLength}: {cachedLength} cached + {stepFrames} frames, {System.Diagnostics.Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds:F1} ms");
            }

            var allLogits = results[0].GetTensorDataAsSpan<float>();
            var allChunkEmbeds = results[1].GetTensorDataAsSpan<float>();
            silence ??= results[2].GetTensorDataAsSpan<float>().ToArray();
            if (allLogits.Length != (inputCached + inputStep) * Subsampling * Speakers || allChunkEmbeds.Length != inputStep * Hidden)
            {
                throw new InvalidOperationException($"unexpected output sizes {allLogits.Length} and {allChunkEmbeds.Length} for {inputCached} cached + {inputStep} chunk frames");
            }

            // Without the padding: the cached frames, then the chunk with its look-ahead.
            var logits = allLogits.Slice(padCached * Subsampling * Speakers, (cachedLength + stepFrames) * Subsampling * Speakers);
            var chunkEmbeds = allChunkEmbeds[..(stepFrames * Hidden)];

            // The chunk's frames, without the cached ones before and the look-ahead after.
            var from = cachedLength * Subsampling * Speakers;
            var count = chunkFrames * Subsampling * Speakers;
            for (var i = 0; i < count; i++)
            {
                output[written + i] = Sigmoid(logits[from + i]);
            }

            written += count;
            state.Update(this, logits, chunkEmbeds, silence, cachedLength, stepFrames, chunkFrames, mask);
        }

        // Without look-ahead the last encoder frame may cover the padding added by feature stacking.
        return output.AsSpan(0, Math.Min(written, frames * Speakers)).ToArray();
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private sealed class State(NemotronProfile profile)
    {
        public readonly float[] CacheEmbeds = new float[profile.SpeakerCacheLength * Hidden];
        public readonly float[] CacheProbs = new float[profile.SpeakerCacheLength * Speakers];
        public readonly float[] FifoEmbeds = new float[(profile.FifoLength + profile.ChunkLength + profile.UpdatePeriod) * Hidden];
        public int CacheCount;
        public int FifoCount;
        public bool Compressed;

        public void Update(
            NemotronStreamingDiarizer owner,
            ReadOnlySpan<float> logits,
            ReadOnlySpan<float> chunkEmbeds,
            float[] silence,
            int cachedLength,
            int stepFrames,
            int chunkFrames,
            long[] mask)
        {
            // Speaker probabilities at the encoder frame rate, zeroed on padding frames.
            var inputFrames = cachedLength + stepFrames;
            var probs = new float[inputFrames * Speakers];
            for (var f = 0; f < inputFrames; f++)
            {
                for (var s = 0; s < Speakers; s++)
                {
                    var sum = 0f;
                    for (var k = 0; k < Subsampling; k++)
                    {
                        sum += Sigmoid(logits[(((f * Subsampling) + k) * Speakers) + s]);
                    }

                    probs[(f * Speakers) + s] = mask[f] != 0 ? sum / Subsampling : 0f;
                }
            }

            var cacheCount = CacheCount;
            var fifoCount = FifoCount;
            // FIFO frames followed by the chunk frames (not the look-ahead ones).
            var total = fifoCount + chunkFrames;
            var fifoEmbeds = new float[total * Hidden];
            Array.Copy(FifoEmbeds, 0, fifoEmbeds, 0, fifoCount * Hidden);
            chunkEmbeds[..(chunkFrames * Hidden)].CopyTo(fifoEmbeds.AsSpan(fifoCount * Hidden));

            var popped = total <= profile.FifoLength ? 0 : Math.Min(Math.Max(profile.UpdatePeriod, total - profile.FifoLength), total);
            if (popped > 0)
            {
                var fifoProbs = new float[total * Speakers];
                Array.Copy(probs, cacheCount * Speakers, fifoProbs, 0, fifoCount * Speakers);
                Array.Copy(probs, cachedLength * Speakers, fifoProbs, fifoCount * Speakers, chunkFrames * Speakers);

                var newCount = cacheCount + popped;
                var embeds = new float[newCount * Hidden];
                var cacheProbs = new float[newCount * Speakers];
                Array.Copy(CacheEmbeds, 0, embeds, 0, cacheCount * Hidden);
                Array.Copy(fifoEmbeds, 0, embeds, cacheCount * Hidden, popped * Hidden);
                // An uncompressed cache holds plain chunk frames whose probabilities this step re-estimates;
                // a compressed one is out of order, so only the stored probabilities fit it.
                Array.Copy(Compressed ? CacheProbs : probs, 0, cacheProbs, 0, cacheCount * Speakers);
                Array.Copy(fifoProbs, 0, cacheProbs, cacheCount * Speakers, popped * Speakers);

                if (newCount > profile.SpeakerCacheLength)
                {
                    (embeds, cacheProbs) = owner.Compress(embeds, cacheProbs, newCount, silence);
                    newCount = profile.SpeakerCacheLength;
                    Compressed = true;
                }

                Array.Copy(embeds, CacheEmbeds, newCount * Hidden);
                Array.Copy(cacheProbs, CacheProbs, newCount * Speakers);
                CacheCount = newCount;
            }

            FifoCount = total - popped;
            Array.Copy(fifoEmbeds, popped * Hidden, FifoEmbeds, 0, FifoCount * Hidden);
        }
    }

    /// <summary>
    /// Keeps the speaker-cache-length most important frames, grouped by speaker and in their original order
    /// within a speaker, with one learned silence frame per speaker.
    /// </summary>
    private (float[] Embeds, float[] Probs) Compress(float[] embeds, float[] probs, int frames, float[] silence)
    {
        var scores = FrameScores(probs, frames);
        for (var f = profile.SpeakerCacheLength; f < frames; f++)
        {
            for (var s = 0; s < Speakers; s++)
            {
                scores[(f * Speakers) + s] += LatestFramesScoreBoost;
            }
        }

        Boost(scores, frames, _strongBoosted, -2f * LogHalf);
        Boost(scores, frames, _weakBoosted, -LogHalf);

        // Per speaker: its frames, then the silence slots that always win (+inf).
        var scored = frames + SilenceFramesPerSpeaker;
        var flat = new (float Score, int Index)[scored * Speakers];
        for (var s = 0; s < Speakers; s++)
        {
            for (var f = 0; f < scored; f++)
            {
                var score = f < frames ? scores[(f * Speakers) + s] : float.PositiveInfinity;
                flat[(s * scored) + f] = (score, (s * scored) + f);
            }
        }

        var top = flat
            .OrderByDescending(x => x.Score)
            .Take(profile.SpeakerCacheLength)
            .Select(x => float.IsNegativeInfinity(x.Score) ? int.MaxValue : x.Index)
            .Order()
            .ToArray();

        var outEmbeds = new float[profile.SpeakerCacheLength * Hidden];
        var outProbs = new float[profile.SpeakerCacheLength * Speakers];
        for (var i = 0; i < top.Length; i++)
        {
            // Silence slots and unfilled places take the silence embedding and zero probabilities.
            var frame = top[i] == int.MaxValue ? frames : Math.Min(top[i] % scored, frames);
            if (frame >= frames)
            {
                silence.CopyTo(outEmbeds, i * Hidden);
            }
            else
            {
                Array.Copy(embeds, frame * Hidden, outEmbeds, i * Hidden, Hidden);
                Array.Copy(probs, frame * Speakers, outProbs, i * Speakers, Speakers);
            }
        }

        return (outEmbeds, outProbs);
    }

    private float[] FrameScores(float[] probs, int frames)
    {
        var scores = new float[frames * Speakers];
        var speech = new bool[frames * Speakers];
        var positives = new int[Speakers];
        for (var f = 0; f < frames; f++)
        {
            var sumComplements = 0f;
            for (var s = 0; s < Speakers; s++)
            {
                sumComplements += MathF.Log(Math.Max(1f - probs[(f * Speakers) + s], PredictionScoreThreshold));
            }

            for (var s = 0; s < Speakers; s++)
            {
                var i = (f * Speakers) + s;
                var p = probs[i];
                var score = MathF.Log(Math.Max(p, PredictionScoreThreshold))
                    - MathF.Log(Math.Max(1f - p, PredictionScoreThreshold))
                    + sumComplements
                    - LogHalf;
                speech[i] = p > 0.5f;
                scores[i] = speech[i] ? score : float.NegativeInfinity;
                if (scores[i] > 0)
                {
                    positives[s]++;
                }
            }
        }

        for (var f = 0; f < frames; f++)
        {
            for (var s = 0; s < Speakers; s++)
            {
                var i = (f * Speakers) + s;
                if (!(scores[i] > 0) && speech[i] && positives[s] >= _minPositiveScores)
                {
                    scores[i] = float.NegativeInfinity;
                }
            }
        }

        return scores;
    }

    /// <summary>Adds <paramref name="boost"/> to each speaker's <paramref name="count"/> highest-scoring frames.</summary>
    private static void Boost(float[] scores, int frames, int count, float boost)
    {
        var column = new (float Score, int Frame)[frames];
        for (var s = 0; s < Speakers; s++)
        {
            for (var f = 0; f < frames; f++)
            {
                column[f] = (scores[(f * Speakers) + s], f);
            }

            foreach (var (_, frame) in column.OrderByDescending(x => x.Score).Take(Math.Min(count, frames)))
            {
                scores[(frame * Speakers) + s] += boost;
            }
        }
    }
}
