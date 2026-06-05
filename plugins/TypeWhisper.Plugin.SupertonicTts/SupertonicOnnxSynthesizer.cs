using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed class SupertonicOnnxSynthesizer : ISupertonicSynthesizer
{
    private readonly SupertonicTextProcessor _textProcessor;
    private readonly InferenceSession _durationPredictor;
    private readonly InferenceSession _textEncoder;
    private readonly InferenceSession _vectorEstimator;
    private readonly InferenceSession _vocoder;
    private readonly SupertonicConfig _config;
    private readonly Dictionary<string, SupertonicVoiceStyle> _voiceStyleCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Performs supertonic onnx synthesizer.
    /// </summary>
    public SupertonicOnnxSynthesizer(string assetRoot)
    {
        var onnxDir = Path.Combine(assetRoot, "onnx");
        var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1,
        };

        _config = SupertonicConfig.Load(Path.Combine(onnxDir, "tts.json"));
        _textProcessor = new SupertonicTextProcessor(Path.Combine(onnxDir, "unicode_indexer.json"));
        _durationPredictor = new InferenceSession(Path.Combine(onnxDir, "duration_predictor.onnx"), options);
        _textEncoder = new InferenceSession(Path.Combine(onnxDir, "text_encoder.onnx"), options);
        _vectorEstimator = new InferenceSession(Path.Combine(onnxDir, "vector_estimator.onnx"), options);
        _vocoder = new InferenceSession(Path.Combine(onnxDir, "vocoder.onnx"), options);
    }

    /// <summary>
    /// Performs synthesize.
    /// </summary>
    public SupertonicSynthesisResult Synthesize(SupertonicSynthesisRequest request, CancellationToken ct)
    {
        var style = GetVoiceStyle(request.VoiceStylePath);
        var samples = new List<float>();
        var chunks = ChunkText(request.Text, request.Language == "ko" || request.Language == "ja" ? 120 : 300);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var result = InferSingle(chunk, request.Language, style, request.DenoisingSteps, (float)request.Speed, ct);
            if (samples.Count > 0)
                samples.AddRange(new float[(int)(0.3 * _config.SampleRate)]);
            samples.AddRange(result);
        }

        return new SupertonicSynthesisResult(samples.ToArray(), _config.SampleRate);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _durationPredictor.Dispose();
        _textEncoder.Dispose();
        _vectorEstimator.Dispose();
        _vocoder.Dispose();
    }

    private float[] InferSingle(
        string text,
        string language,
        SupertonicVoiceStyle style,
        int totalSteps,
        float speed,
        CancellationToken ct)
    {
        var features = _textProcessor.Process([text], [language]);
        float[] duration;
        DenseTensor<float> textEmbedding;

        using (var durationOutputs = _durationPredictor.Run(
        [
            NamedOnnxValue.CreateFromTensor("text_ids", features.TextIds),
            NamedOnnxValue.CreateFromTensor("style_dp", style.Dp),
            NamedOnnxValue.CreateFromTensor("text_mask", features.TextMask),
        ]))
        {
            duration = durationOutputs.First(output => output.Name == "duration").AsTensor<float>().ToArray();
        }

        for (var i = 0; i < duration.Length; i++)
            duration[i] /= speed;

        using (var textEncoderOutputs = _textEncoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("text_ids", features.TextIds),
            NamedOnnxValue.CreateFromTensor("style_ttl", style.Ttl),
            NamedOnnxValue.CreateFromTensor("text_mask", features.TextMask),
        ]))
        {
            textEmbedding = CopyTensor(textEncoderOutputs.First(output => output.Name == "text_emb").AsTensor<float>());
        }

        var latentDim = _config.LatentDim * _config.ChunkCompressFactor;
        var chunkSize = _config.BaseChunkSize * _config.ChunkCompressFactor;
        var wavLength = Math.Max(1, (long)Math.Ceiling(duration.Max() * _config.SampleRate));
        var latentLength = Math.Max(1, (int)((wavLength + chunkSize - 1) / chunkSize));
        var latent = SampleNoisyLatent(latentDim, latentLength);
        var latentMask = BuildLatentMask(wavLength, chunkSize, latentLength);

        for (var step = 0; step < totalSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            using var vectorOutputs = _vectorEstimator.Run(
            [
                NamedOnnxValue.CreateFromTensor("noisy_latent", new DenseTensor<float>(latent, new[] { 1, latentDim, latentLength })),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmbedding),
                NamedOnnxValue.CreateFromTensor("style_ttl", style.Ttl),
                NamedOnnxValue.CreateFromTensor("text_mask", features.TextMask),
                NamedOnnxValue.CreateFromTensor("latent_mask", new DenseTensor<float>(latentMask, new[] { 1, 1, latentLength })),
                NamedOnnxValue.CreateFromTensor("total_step", new DenseTensor<float>(new[] { (float)totalSteps }, new[] { 1 })),
                NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(new[] { (float)step }, new[] { 1 })),
            ]);
            latent = vectorOutputs.First(output => output.Name == "denoised_latent").AsTensor<float>().ToArray();
        }

        using var vocoderOutputs = _vocoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("latent", new DenseTensor<float>(latent, new[] { 1, latentDim, latentLength })),
        ]);
        var wav = vocoderOutputs.First(output => output.Name == "wav_tts").AsTensor<float>().ToArray();
        var trimLength = Math.Min(wav.Length, Math.Max(0, (int)(duration[0] * _config.SampleRate)));
        return trimLength == wav.Length ? wav : wav.Take(trimLength).ToArray();
    }

    private SupertonicVoiceStyle GetVoiceStyle(string voiceStylePath)
    {
        if (_voiceStyleCache.TryGetValue(voiceStylePath, out var style))
            return style;

        style = SupertonicVoiceStyleLoader.Load(voiceStylePath);
        _voiceStyleCache[voiceStylePath] = style;
        return style;
    }

    private static DenseTensor<float> CopyTensor(Tensor<float> tensor) =>
        new(tensor.ToArray(), tensor.Dimensions.ToArray());

    private static float[] SampleNoisyLatent(int latentDim, int latentLength)
    {
        var values = new float[latentDim * latentLength];
        for (var i = 0; i < values.Length; i += 2)
        {
            var u1 = 1.0 - Random.Shared.NextDouble();
            var u2 = 1.0 - Random.Shared.NextDouble();
            var radius = Math.Sqrt(-2.0 * Math.Log(u1));
            var angle = 2.0 * Math.PI * u2;
            values[i] = (float)(radius * Math.Cos(angle));
            if (i + 1 < values.Length)
                values[i + 1] = (float)(radius * Math.Sin(angle));
        }

        return values;
    }

    private static float[] BuildLatentMask(long wavLength, int chunkSize, int latentLength)
    {
        var activeLength = Math.Min(latentLength, Math.Max(1, (int)((wavLength + chunkSize - 1) / chunkSize)));
        var mask = new float[latentLength];
        Array.Fill(mask, 1.0f, 0, activeLength);
        return mask;
    }

    private static IReadOnlyList<string> ChunkText(string text, int maxLength)
    {
        var chunks = new List<string>();
        foreach (var paragraph in Regex.Split(text.Trim(), @"\n\s*\n+").Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var remaining = paragraph.Trim();
            while (remaining.Length > maxLength)
            {
                var split = FindSplitIndex(remaining, maxLength);
                chunks.Add(remaining[..split].Trim());
                remaining = remaining[split..].Trim();
            }

            if (remaining.Length > 0)
                chunks.Add(remaining);
        }

        return chunks.Count == 0 ? [text] : chunks;
    }

    private static int FindSplitIndex(string text, int maxLength)
    {
        var searchLength = Math.Min(maxLength, text.Length);
        for (var i = searchLength - 1; i >= 0; i--)
        {
            if (text[i] is '.' or '!' or '?' or ';')
                return i + 1;
        }

        for (var i = searchLength - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return i + 1;
        }

        return searchLength;
    }

    private sealed record SupertonicConfig(
        int SampleRate,
        int BaseChunkSize,
        int ChunkCompressFactor,
        int LatentDim)
    {
        /// <summary>
        /// Loads persisted state from storage.
        /// </summary>
        public static SupertonicConfig Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new SupertonicConfig(
                root.GetProperty("ae").GetProperty("sample_rate").GetInt32(),
                root.GetProperty("ae").GetProperty("base_chunk_size").GetInt32(),
                root.GetProperty("ttl").GetProperty("chunk_compress_factor").GetInt32(),
                root.GetProperty("ttl").GetProperty("latent_dim").GetInt32());
        }
    }
}
