using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TypeWhisper.Plugin.GigaAm;

/// <summary>Transcribes one chunk of 16 kHz mono audio.</summary>
internal interface IGigaAmRecognizer : IDisposable
{
    string Transcribe(float[] samples, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the GigaAM v3 end-to-end RNN-T model (Russian, with punctuation and capitalization) through ONNX Runtime on
/// the CPU. The pipeline matches onnx-asr (MIT, https://github.com/istupakov/onnx-asr), which published the ONNX
/// exports, and the macOS plugin: a small bundled graph turns 16 kHz audio into 64 log-mel features, the Conformer
/// encoder runs once per chunk, and a greedy transducer loop calls the decoder and joint networks.
/// </summary>
internal sealed class GigaAmRuntime : IGigaAmRecognizer
{
    internal const int SampleRate = 16000;
    private const int PredictionHiddenSize = 320;
    // From the model's config.json, as onnx-asr reads it.
    private const int MaxTokensPerStep = 3;

    private readonly InferenceSession _preprocessor;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _joint;
    private readonly GigaAmVocabulary _vocabulary;

    public GigaAmRuntime(string modelDirectory, string preprocessorPath)
    {
        _vocabulary = GigaAmVocabulary.Load(Path.Join(modelDirectory, GigaAmModelFiles.Vocabulary));
        using var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2), InterOpNumThreads = 1 };
        var sessions = new List<InferenceSession>();
        InferenceSession Load(string path)
        {
            var session = new InferenceSession(path, options);
            sessions.Add(session);
            return session;
        }
        try
        {
            _preprocessor = Load(preprocessorPath);
            _encoder = Load(Path.Join(modelDirectory, GigaAmModelFiles.Encoder));
            _decoder = Load(Path.Join(modelDirectory, GigaAmModelFiles.Decoder));
            _joint = Load(Path.Join(modelDirectory, GigaAmModelFiles.Joint));
        }
        catch
        {
            foreach (var session in sessions) session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length == 0) return "";
        // The feature extractor does not center its frames, so very short input would yield none.
        var (features, featureFrames) = ExtractFeatures(PadToMinimumDuration(samples));
        cancellationToken.ThrowIfCancellationRequested();
        var (encoded, dimension, frames) = Encode(features, featureFrames);
        cancellationToken.ThrowIfCancellationRequested();
        return _vocabulary.Text(DecodeGreedy(encoded, dimension, frames, cancellationToken));
    }

    internal static float[] PadToMinimumDuration(float[] samples)
    {
        if (samples.Length >= SampleRate) return samples;
        var padded = new float[SampleRate];
        samples.CopyTo(padded, 0);
        return padded;
    }

    private (DenseTensor<float> Features, long Frames) ExtractFeatures(float[] samples)
    {
        using var outputs = _preprocessor.Run([
            NamedOnnxValue.CreateFromTensor("waveforms", new DenseTensor<float>(samples, [1, samples.Length])),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", new DenseTensor<long>(new long[] { samples.Length }, [1]))]);
        var features = Output(outputs, "features").AsTensor<float>();
        var frames = Output(outputs, "features_lens").AsTensor<long>().GetValue(0);
        // The outputs are released with their session results, so the features are copied for the encoder.
        return (new DenseTensor<float>(features.ToArray(), features.Dimensions), frames);
    }

    /// <summary>Returns the valid encoder frames as frame-major vectors.</summary>
    private (float[] Encoded, int Dimension, int Frames) Encode(DenseTensor<float> features, long featureFrames)
    {
        using var outputs = _encoder.Run([
            NamedOnnxValue.CreateFromTensor("audio_signal", features),
            NamedOnnxValue.CreateFromTensor("length", new DenseTensor<long>(new long[] { featureFrames }, [1]))]);
        var encoded = Output(outputs, "encoded").AsTensor<float>();
        if (encoded.Dimensions.Length != 3 || encoded.Dimensions[0] != 1)
            throw new InvalidDataException($"GigaAM returned an unexpected encoder shape [{string.Join(", ", encoded.Dimensions.ToArray())}].");
        var dimension = encoded.Dimensions[1];
        var totalFrames = encoded.Dimensions[2];
        var frames = Math.Clamp(Output(outputs, "encoded_len").AsTensor<int>().GetValue(0), 0, totalFrames);
        // The encoder emits [1, dimension, frames]; the joint network takes one frame at a time.
        var flat = encoded.ToArray();
        var frameMajor = new float[frames * dimension];
        for (var frame = 0; frame < frames; frame++)
            for (var index = 0; index < dimension; index++)
                frameMajor[frame * dimension + index] = flat[index * totalFrames + frame];
        return (frameMajor, dimension, frames);
    }

    private int[] DecodeGreedy(float[] encoded, int dimension, int frames, CancellationToken cancellationToken)
    {
        var blank = _vocabulary.BlankId;
        var hidden = new float[PredictionHiddenSize];
        var cell = new float[PredictionHiddenSize];
        var lastToken = blank;
        // The decoder depends only on the last emitted token, so its output is reused while the joint network keeps
        // predicting blanks.
        Prediction? prediction = null;
        var frameVector = new float[dimension];
        var tokens = new List<int>();
        var emittedThisFrame = 0;
        for (var frame = 0; frame < frames;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prediction ??= RunDecoder(lastToken, hidden, cell);
            Array.Copy(encoded, frame * dimension, frameVector, 0, dimension);
            var token = Argmax(RunJoint(frameVector, prediction.Output));
            if (token != blank)
            {
                tokens.Add(token);
                lastToken = token;
                hidden = prediction.Hidden;
                cell = prediction.Cell;
                prediction = null;
                emittedThisFrame++;
            }
            if (token == blank || emittedThisFrame == MaxTokensPerStep)
            {
                frame++;
                emittedThisFrame = 0;
            }
        }
        return [.. tokens];
    }

    private Prediction RunDecoder(int token, float[] hidden, float[] cell)
    {
        using var outputs = _decoder.Run([
            NamedOnnxValue.CreateFromTensor("x", new DenseTensor<long>(new long[] { token }, [1, 1])),
            NamedOnnxValue.CreateFromTensor("h.1", new DenseTensor<float>(hidden, [1, 1, PredictionHiddenSize])),
            NamedOnnxValue.CreateFromTensor("c.1", new DenseTensor<float>(cell, [1, 1, PredictionHiddenSize]))]);
        // [1, 1, 320] and the joint input [1, 320, 1] share one memory layout.
        return new(Output(outputs, "dec").AsTensor<float>().ToArray(), Output(outputs, "h").AsTensor<float>().ToArray(),
            Output(outputs, "c").AsTensor<float>().ToArray());
    }

    private float[] RunJoint(float[] frame, float[] prediction)
    {
        using var outputs = _joint.Run([
            NamedOnnxValue.CreateFromTensor("enc", new DenseTensor<float>(frame, [1, frame.Length, 1])),
            NamedOnnxValue.CreateFromTensor("dec", new DenseTensor<float>(prediction, [1, PredictionHiddenSize, 1]))]);
        return Output(outputs, "joint").AsTensor<float>().ToArray();
    }

    private static DisposableNamedOnnxValue Output(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, string name) =>
        outputs.FirstOrDefault(output => output.Name == name) ?? throw new InvalidDataException($"GigaAM returned no {name} output.");

    /// <summary>The index of the first maximum.</summary>
    internal static int Argmax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var index = 1; index < values.Length; index++)
            if (values[index] > values[best]) best = index;
        return best;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _preprocessor.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
        _joint.Dispose();
    }

    private sealed record Prediction(float[] Output, float[] Hidden, float[] Cell);
}
