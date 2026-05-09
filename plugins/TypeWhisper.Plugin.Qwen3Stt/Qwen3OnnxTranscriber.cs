using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TypeWhisper.Plugin.Qwen3Stt;

internal sealed class Qwen3OnnxTranscriber : IQwen3Transcriber
{
    private const int PrimaryMaxTokens = 2048;
    private const int FallbackMaxTokens = 1536;
    private const double PrimaryChunkSeconds = 30.0;
    private const double FallbackChunkSeconds = 15.0;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderInit;
    private readonly InferenceSession _decoderStep;
    private readonly QwenTokenizer _tokenizer;
    private readonly Half[] _embeddings;
    private readonly int _hiddenSize;
    private bool _disposed;

    private Qwen3OnnxTranscriber(
        Qwen3ModelDefinition model,
        InferenceSession encoder,
        InferenceSession decoderInit,
        InferenceSession decoderStep,
        QwenTokenizer tokenizer,
        Half[] embeddings,
        bool directMl)
    {
        _encoder = encoder;
        _decoderInit = decoderInit;
        _decoderStep = decoderStep;
        _tokenizer = tokenizer;
        _embeddings = embeddings;
        _hiddenSize = model.HiddenSize;
        UsesDirectMl = directMl;
    }

    public bool UsesDirectMl { get; }

    public static Qwen3OnnxTranscriber Load(string modelDirectory, Qwen3ModelDefinition model)
    {
        if (!Qwen3ModelCatalog.IsDirectMlOptInEnabled())
            return Load(modelDirectory, model, useDirectMl: false);

        try
        {
            return Load(modelDirectory, model, useDirectMl: true);
        }
        catch
        {
            return Load(modelDirectory, model, useDirectMl: false);
        }
    }

    public static Qwen3OnnxTranscriber LoadCpu(string modelDirectory, Qwen3ModelDefinition model) =>
        Load(modelDirectory, model, useDirectMl: false);

    public Qwen3Transcription Transcribe(byte[] wavAudio, string? language, string? prompt, CancellationToken ct)
    {
        var (samples, _) = Qwen3WavReader.DecodeMono(wavAudio);
        var duration = samples.Length / 16000.0;
        var languageName = Qwen3LanguageMapper.ResolveLanguageName(language);
        var context = Qwen3ContextBiasFormatter.Format(prompt);

        var primaryRaw = Generate(samples, languageName, context, PrimaryChunkSeconds, PrimaryMaxTokens, ct);
        var primary = Qwen3AsrOutputParser.Parse(primaryRaw, languageName);
        var text = NormalizeTranscript(primary.Text);
        var resultLanguageName = primary.LanguageName ?? languageName;

        if (QwenTranscriptGuard.IsLikelyLooped(text))
        {
            var fallbackRaw = Generate(samples, languageName, string.Empty, FallbackChunkSeconds, FallbackMaxTokens, ct);
            var fallback = Qwen3AsrOutputParser.Parse(fallbackRaw, languageName);
            var fallbackText = NormalizeTranscript(fallback.Text);

            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                text = QwenTranscriptGuard.IsLikelyLooped(fallbackText)
                    ? QwenTranscriptGuard.PreferredTranscript(text, fallbackText)
                    : fallbackText;
                resultLanguageName = fallback.LanguageName ?? resultLanguageName;
            }
        }

        var cleaned = QwenTranscriptGuard.RemovingLikelyTrailingArtifact(text, resultLanguageName);
        var detectedLanguage = Qwen3LanguageMapper.LanguageCodeForQwenLanguageName(resultLanguageName) ?? language;
        return new Qwen3Transcription(cleaned, detectedLanguage, duration);
    }

    private static Qwen3OnnxTranscriber Load(string modelDirectory, Qwen3ModelDefinition model, bool useDirectMl)
    {
        var options = CreateSessionOptions(useDirectMl);
        InferenceSession? encoder = null;
        InferenceSession? decoderInit = null;
        InferenceSession? decoderStep = null;

        try
        {
            encoder = new InferenceSession(Path.Combine(modelDirectory, model.EncoderFileName), options);
            decoderInit = new InferenceSession(Path.Combine(modelDirectory, model.DecoderInitFileName), CreateSessionOptions(useDirectMl));
            decoderStep = new InferenceSession(Path.Combine(modelDirectory, model.DecoderStepFileName), CreateSessionOptions(useDirectMl));
            var tokenizer = QwenTokenizer.Load(modelDirectory);
            var embeddings = LoadEmbeddings(Path.Combine(modelDirectory, "embed_tokens.bin"));
            return new Qwen3OnnxTranscriber(model, encoder, decoderInit, decoderStep, tokenizer, embeddings, useDirectMl);
        }
        catch
        {
            encoder?.Dispose();
            decoderInit?.Dispose();
            decoderStep?.Dispose();
            throw;
        }
    }

    private static SessionOptions CreateSessionOptions(bool useDirectMl)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };

        if (useDirectMl)
        {
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
        }

        return options;
    }

    private string Generate(
        float[] samples,
        string? languageName,
        string context,
        double chunkSeconds,
        int maxTokens,
        CancellationToken ct)
    {
        var chunkSize = Math.Max(1, (int)(chunkSeconds * 16000));
        var outputs = new List<string>();
        for (var start = 0; start < samples.Length; start += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, samples.Length - start);
            var chunk = new float[length];
            Array.Copy(samples, start, chunk, 0, length);
            outputs.Add(GenerateChunk(chunk, languageName, context, maxTokens, ct));
        }

        return string.Concat(outputs);
    }

    private string GenerateChunk(float[] samples, string? languageName, string context, int maxTokens, CancellationToken ct)
    {
        var (features, frames) = Qwen3AudioPreprocessor.ComputeLogMel(samples);
        var featuresTensor = new DenseTensor<float>(features, new[] { 1, 128, frames });
        var encoderInputName = _encoder.InputMetadata.Keys.First();

        using var encoderOutputs = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor(encoderInputName, featuresTensor)
        ]);

        var audioFeatures = GetFirstFloatTensor(encoderOutputs)
            ?? throw new InvalidOperationException("Qwen3 encoder did not return a float tensor.");
        var audioTokenCount = InferAudioTokenCount(audioFeatures, frames);

        var prompt = Qwen3PromptBuilder.Build(context, languageName, audioTokenCount);
        var inputIds = _tokenizer.EncodePrompt(prompt.Text).ToArray();
        var audioOffset = Array.IndexOf(inputIds, QwenTokenizer.AudioPadTokenId);
        if (audioOffset < 0)
            audioOffset = inputIds.Length;

        using var decoderOutputs = RunDecoderInit(inputIds, audioFeatures, audioOffset, audioTokenCount);
        return DecodeAutoregressive(decoderOutputs, maxTokens, inputIds.Length, ct);
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunDecoderInit(
        int[] inputIds,
        Tensor<float> audioFeatures,
        int audioOffset,
        int audioTokenCount)
    {
        var inputs = CreateDecoderInitInputs(
            _decoderInit.InputMetadata.Keys,
            inputIds,
            audioFeatures,
            audioOffset,
            audioTokenCount);

        return _decoderInit.Run(inputs);
    }

    internal static IReadOnlyList<NamedOnnxValue> CreateDecoderInitInputs(
        IEnumerable<string> inputNames,
        int[] inputIds,
        Tensor<float> audioFeatures,
        int audioOffset,
        int audioTokenCount)
    {
        var inputs = new List<NamedOnnxValue>();
        foreach (var name in inputNames)
        {
            if (name.Contains("input_ids", StringComparison.OrdinalIgnoreCase))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name,
                    new DenseTensor<long>(inputIds.Select(id => (long)id).ToArray(), new[] { 1, inputIds.Length })));
            }
            else if (IsPositionIdsInput(name))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, CreatePositionIds(start: 0, length: inputIds.Length)));
            }
            else if (IsAudioLengthInput(name))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { (long)audioTokenCount }, new[] { 1 })));
            }
            else if (IsOffsetInput(name))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { (long)audioOffset }, new[] { 1 })));
            }
            else if (IsAudioFeatureInput(name))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, audioFeatures));
            }
        }

        return inputs;
    }

    private string DecodeAutoregressive(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> initialOutputs,
        int maxTokens,
        int initialSequenceLength,
        CancellationToken ct)
    {
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? currentOutputs = initialOutputs;
        var generated = new List<int>();
        var tokenId = ArgmaxToken(GetLogitsValue(currentOutputs));

        for (var step = 0; step < maxTokens; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (tokenId is QwenTokenizer.ImEndTokenId or QwenTokenizer.EndOfTextTokenId)
                break;

            generated.Add(tokenId);
            var positionId = initialSequenceLength + generated.Count - 1;
            var nextOutputs = RunDecoderStep(tokenId, currentOutputs, positionId);
            if (!ReferenceEquals(currentOutputs, initialOutputs))
                currentOutputs.Dispose();

            currentOutputs = nextOutputs;
            tokenId = ArgmaxToken(GetLogitsValue(currentOutputs));
        }

        if (!ReferenceEquals(currentOutputs, initialOutputs))
            currentOutputs?.Dispose();

        return _tokenizer.Decode(generated);
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunDecoderStep(
        int tokenId,
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> previousOutputs,
        int positionId)
    {
        var cacheValues = previousOutputs
            .Where(output => !output.Name.Contains("logits", StringComparison.OrdinalIgnoreCase))
            .Select(output => output.Value)
            .ToList();

        var inputs = CreateDecoderStepInputs(
            _decoderStep.InputMetadata.Select(input => new Qwen3OnnxInputSpec(input.Key, input.Value.ElementType)),
            elementType => CreateEmbeddingTensor(tokenId, elementType),
            cacheValues,
            positionId);

        return _decoderStep.Run(inputs);
    }

    internal static IReadOnlyList<NamedOnnxValue> CreateDecoderStepInputs(
        IEnumerable<string> inputNames,
        Tensor<Half> inputEmbedding,
        IReadOnlyList<object> cacheValues,
        int positionId) =>
        CreateDecoderStepInputs(
            inputNames.Select(name => new Qwen3OnnxInputSpec(name, typeof(Half))),
            _ => inputEmbedding,
            cacheValues,
            positionId);

    internal static IReadOnlyList<NamedOnnxValue> CreateDecoderStepInputs(
        IEnumerable<Qwen3OnnxInputSpec> inputSpecs,
        Func<Type, object> createEmbeddingTensor,
        IReadOnlyList<object> cacheValues,
        int positionId)
    {
        var inputs = new List<NamedOnnxValue>();
        var cacheIndex = 0;

        foreach (var spec in inputSpecs)
        {
            var name = spec.Name;
            if (name.Contains("input_embed", StringComparison.OrdinalIgnoreCase)
                || name.Contains("input_embeds", StringComparison.OrdinalIgnoreCase))
            {
                inputs.Add(CreateTensorInput(name, createEmbeddingTensor(spec.ElementType)));
            }
            else if (IsPositionIdsInput(name))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, CreatePositionIds(positionId, length: 1)));
            }
            else if (IsPastSequenceLengthInput(name))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { (long)positionId }, new[] { 1 })));
            }
            else if (name.Contains("cache_position", StringComparison.OrdinalIgnoreCase))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { (long)positionId }, new[] { 1 })));
            }
            else if (IsOffsetInput(name))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { (long)positionId }, new[] { 1 })));
            }
            else if (cacheIndex < cacheValues.Count)
            {
                inputs.Add(CreateTensorInput(name, cacheValues[cacheIndex]));
                cacheIndex++;
            }
        }

        return inputs;
    }

    private object CreateEmbeddingTensor(int tokenId, Type elementType)
    {
        if (elementType == typeof(float))
            return CreateFloatEmbeddingTensor(tokenId);
        if (elementType == typeof(Half))
            return CreateHalfEmbeddingTensor(tokenId);

        throw new InvalidOperationException($"Unsupported Qwen3 decoder embedding input type: {elementType.Name}.");
    }

    private DenseTensor<float> CreateFloatEmbeddingTensor(int tokenId)
    {
        var start = checked(tokenId * _hiddenSize);
        var data = new float[_hiddenSize];
        for (var i = 0; i < _hiddenSize; i++)
            data[i] = (float)_embeddings[start + i];
        return new DenseTensor<float>(data, new[] { 1, 1, _hiddenSize });
    }

    private DenseTensor<Half> CreateHalfEmbeddingTensor(int tokenId)
    {
        var start = checked(tokenId * _hiddenSize);
        var data = new Half[_hiddenSize];
        Array.Copy(_embeddings, start, data, 0, _hiddenSize);
        return new DenseTensor<Half>(data, new[] { 1, 1, _hiddenSize });
    }

    private static Tensor<float>? GetFirstFloatTensor(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> values)
    {
        foreach (var value in values)
        {
            if (value.Value is Tensor<float> floatTensor)
                return floatTensor;
        }
        return null;
    }

    private static object GetLogitsValue(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> values)
    {
        return values.FirstOrDefault(value => value.Name.Contains("logits", StringComparison.OrdinalIgnoreCase))?.Value
            ?? values.First().Value;
    }

    private static NamedOnnxValue CreateTensorInput(string name, object value)
    {
        return value switch
        {
            Tensor<float> tensor => NamedOnnxValue.CreateFromTensor(name, tensor),
            Tensor<Half> tensor => NamedOnnxValue.CreateFromTensor(name, tensor),
            Tensor<long> tensor => NamedOnnxValue.CreateFromTensor(name, tensor),
            Tensor<int> tensor => NamedOnnxValue.CreateFromTensor(name, tensor),
            _ => throw new InvalidOperationException($"Unexpected ONNX tensor type: {value.GetType().Name}")
        };
    }

    private static DenseTensor<long> CreatePositionIds(int start, int length) =>
        new(Enumerable.Range(start, length).Select(value => (long)value).ToArray(), new[] { 1, length });

    private static bool IsPositionIdsInput(string name) =>
        name.Contains("position_ids", StringComparison.OrdinalIgnoreCase);

    private static bool IsPastSequenceLengthInput(string name) =>
        name.Contains("past_seq_len", StringComparison.OrdinalIgnoreCase)
        || name.Contains("past_sequence_length", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioLengthInput(string name) =>
        name.Contains("audio_len", StringComparison.OrdinalIgnoreCase)
        || name.Contains("audio_length", StringComparison.OrdinalIgnoreCase);

    private static bool IsOffsetInput(string name) =>
        name.Equals("offset", StringComparison.OrdinalIgnoreCase)
        || name.Contains("audio_offset", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioFeatureInput(string name) =>
        name.Contains("audio", StringComparison.OrdinalIgnoreCase)
        && !IsAudioLengthInput(name)
        && !IsOffsetInput(name);

    private static int ArgmaxToken(object logitsValue)
    {
        return logitsValue switch
        {
            DenseTensor<float> tensor => Argmax(tensor.Buffer.Span, (int)tensor.Dimensions[^1], OffsetForLastToken(tensor)),
            DenseTensor<Half> tensor => Argmax(tensor.Buffer.Span, (int)tensor.Dimensions[^1], OffsetForLastToken(tensor)),
            Tensor<float> tensor => Argmax(tensor.ToArray(), (int)tensor.Dimensions[^1], OffsetForLastToken(tensor)),
            Tensor<Half> tensor => Argmax(tensor.ToArray(), (int)tensor.Dimensions[^1], OffsetForLastToken(tensor)),
            _ => throw new InvalidOperationException($"Unexpected logits tensor type: {logitsValue.GetType().Name}")
        };
    }

    private static int Argmax(ReadOnlySpan<float> values, int vocabSize, int offset)
    {
        var bestId = 0;
        var bestValue = float.NegativeInfinity;
        for (var i = 0; i < vocabSize && offset + i < values.Length; i++)
        {
            if (values[offset + i] > bestValue)
            {
                bestValue = values[offset + i];
                bestId = i;
            }
        }
        return bestId;
    }

    private static int Argmax(ReadOnlySpan<Half> values, int vocabSize, int offset)
    {
        var bestId = 0;
        var bestValue = float.NegativeInfinity;
        for (var i = 0; i < vocabSize && offset + i < values.Length; i++)
        {
            var value = (float)values[offset + i];
            if (value > bestValue)
            {
                bestValue = value;
                bestId = i;
            }
        }
        return bestId;
    }

    private static int OffsetForLastToken<T>(Tensor<T> tensor)
    {
        if (tensor.Dimensions.Length < 2)
            return 0;

        var vocabSize = (int)tensor.Dimensions[^1];
        var tokenCount = tensor.Dimensions.Length >= 3 ? (int)tensor.Dimensions[^2] : 1;
        return Math.Max(0, tokenCount - 1) * vocabSize;
    }

    private static int InferAudioTokenCount(Tensor<float> audioFeatures, int melFrames)
    {
        if (audioFeatures.Dimensions.Length >= 3)
            return Math.Max(1, (int)audioFeatures.Dimensions[1]);
        return Math.Max(1, Qwen3AudioPreprocessor.FeatureOutputLength(melFrames));
    }

    private static Half[] LoadEmbeddings(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var result = new Half[bytes.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var bits = BitConverter.ToUInt16(bytes, i * 2);
            result[i] = BitConverter.UInt16BitsToHalf(bits);
        }
        return result;
    }

    private static string NormalizeTranscript(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public void Dispose()
    {
        if (_disposed)
            return;

        _encoder.Dispose();
        _decoderInit.Dispose();
        _decoderStep.Dispose();
        _disposed = true;
    }
}

internal readonly record struct Qwen3OnnxInputSpec(string Name, Type ElementType);

internal sealed record Qwen3Transcription(string Text, string? DetectedLanguage, double DurationSeconds);
