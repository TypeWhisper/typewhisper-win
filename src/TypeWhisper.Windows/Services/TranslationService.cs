using System.IO;
using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Cloud;
using TypeWhisper.Core.Translation;

namespace TypeWhisper.Windows.Services;

public sealed class TranslationService : ITranslationService, IDisposable
{
    private readonly IReadOnlyList<CloudProviderBase> _cloudProviders;
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private readonly Dictionary<string, LoadedTranslationModel> _loadedModels = new();
    private readonly HashSet<string> _loadingModels = new();
    private bool _disposed;

    public TranslationService(IReadOnlyList<CloudProviderBase> cloudProviders)
    {
        _cloudProviders = cloudProviders;
    }

    public bool IsModelReady(string sourceLang, string targetLang)
    {
        // Cloud translation is always ready when a provider is configured
        if (GetConfiguredTranslationProvider() is not null)
            return true;
        return _loadedModels.ContainsKey(ModelKey(sourceLang, targetLang));
    }

    public bool IsModelLoading(string sourceLang, string targetLang) =>
        _loadingModels.Contains(ModelKey(sourceLang, targetLang));

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (sourceLang == targetLang) return text;

        // Prefer cloud LLM translation when available (faster, supports all language pairs)
        var cloudProvider = GetConfiguredTranslationProvider();
        if (cloudProvider is not null)
        {
            return await cloudProvider.TranslateAsync(text, sourceLang, targetLang, ct);
        }

        // Fallback: local ONNX Marian models
        return await TranslateLocalAsync(text, sourceLang, targetLang, ct);
    }

    private CloudProviderBase? GetConfiguredTranslationProvider() =>
        _cloudProviders.FirstOrDefault(p => p.SupportsTranslation);

    private async Task<string> TranslateLocalAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        // Direct model available?
        var directModel = TranslationModelInfo.FindModel(sourceLang, targetLang);
        if (directModel is not null)
        {
            var model = await GetOrLoadModelAsync(sourceLang, targetLang, ct);
            return await Task.Run(() => RunInference(model, text), ct);
        }

        // Chain through English: source→en + en→target
        if (sourceLang != "en" && targetLang != "en")
        {
            var toEn = TranslationModelInfo.FindModel(sourceLang, "en");
            var fromEn = TranslationModelInfo.FindModel("en", targetLang);
            if (toEn is not null && fromEn is not null)
            {
                var model1 = await GetOrLoadModelAsync(sourceLang, "en", ct);
                var english = await Task.Run(() => RunInference(model1, text), ct);

                var model2 = await GetOrLoadModelAsync("en", targetLang, ct);
                return await Task.Run(() => RunInference(model2, english), ct);
            }
        }

        throw new NotSupportedException($"Keine Übersetzung verfügbar für {sourceLang}→{targetLang}");
    }

    private async Task<LoadedTranslationModel> GetOrLoadModelAsync(string sourceLang, string targetLang, CancellationToken ct)
    {
        var key = ModelKey(sourceLang, targetLang);
        if (_loadedModels.TryGetValue(key, out var model))
            return model;
        return await EnsureModelLoadedAsync(sourceLang, targetLang, ct);
    }

    private async Task<LoadedTranslationModel> EnsureModelLoadedAsync(string sourceLang, string targetLang, CancellationToken ct)
    {
        var key = ModelKey(sourceLang, targetLang);

        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            if (_loadedModels.TryGetValue(key, out var existing))
                return existing;

            _loadingModels.Add(key);

            var modelInfo = TranslationModelInfo.FindModel(sourceLang, targetLang)
                ?? throw new NotSupportedException($"No translation model for {sourceLang}→{targetLang}");

            var modelDir = Path.Combine(TypeWhisperEnvironment.ModelsPath, modelInfo.SubDirectory);
            Directory.CreateDirectory(modelDir);

            await DownloadMissingFilesAsync(modelInfo, modelDir, ct);

            var loaded = LoadModel(modelDir);

            _loadedModels[key] = loaded;
            _loadingModels.Remove(key);

            return loaded;
        }
        catch
        {
            _loadingModels.Remove(key);
            throw;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private async Task DownloadMissingFilesAsync(TranslationModelInfo modelInfo, string modelDir, CancellationToken ct)
    {
        foreach (var file in modelInfo.Files)
        {
            var filePath = Path.Combine(modelDir, file.FileName);
            if (File.Exists(filePath)) continue;

            System.Diagnostics.Debug.WriteLine($"Downloading translation model file: {file.FileName}");

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var tmpPath = filePath + ".tmp";
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await contentStream.CopyToAsync(fileStream, ct);
            }

            File.Move(tmpPath, filePath, overwrite: true);
        }
    }

    private static LoadedTranslationModel LoadModel(string modelDir)
    {
        var config = MarianConfig.Load(Path.Combine(modelDir, "config.json"));
        var tokenizer = MarianTokenizer.Load(Path.Combine(modelDir, "tokenizer.json"), config.EosTokenId);

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Environment.ProcessorCount
        };

        var encoder = new InferenceSession(Path.Combine(modelDir, "encoder_model_quantized.onnx"), sessionOptions);
        var decoder = new InferenceSession(Path.Combine(modelDir, "decoder_model_quantized.onnx"), sessionOptions);

        return new LoadedTranslationModel(encoder, decoder, tokenizer, config);
    }

    private static string RunInference(LoadedTranslationModel model, string text)
    {
        var inputIds = model.Tokenizer.Encode(text);
        var seqLen = inputIds.Length;

        var inputIdsTensor = new DenseTensor<long>(inputIds.Select(i => (long)i).ToArray(), [1, seqLen]);
        var attentionMask = new DenseTensor<long>(Enumerable.Repeat(1L, seqLen).ToArray(), [1, seqLen]);

        using var encoderResults = model.Encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        ]);

        var encoderHidden = encoderResults.First().Value as DenseTensor<float>
            ?? throw new InvalidOperationException("Encoder output is not a float tensor");

        var maxTokens = Math.Min(model.Config.MaxLength, 200);
        var decodedIds = new List<int> { model.Config.DecoderStartTokenId };

        for (var step = 0; step < maxTokens; step++)
        {
            var decoderLen = decodedIds.Count;
            var decoderInputIds = new DenseTensor<long>(
                decodedIds.Select(i => (long)i).ToArray(), [1, decoderLen]);

            var decoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", decoderInputIds),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden)
            };

            using var decoderResults = model.Decoder.Run(decoderInputs);
            var logits = decoderResults.First().Value as DenseTensor<float>
                ?? throw new InvalidOperationException("Decoder output is not a float tensor");

            var vocabSize = logits.Dimensions[2];
            var lastTokenOffset = (decoderLen - 1) * vocabSize;
            var bestId = 0;
            var bestVal = float.NegativeInfinity;
            for (var v = 0; v < vocabSize; v++)
            {
                var val = logits.Buffer.Span[lastTokenOffset + v];
                if (val > bestVal)
                {
                    bestVal = val;
                    bestId = v;
                }
            }

            if (bestId == model.Config.EosTokenId) break;
            decodedIds.Add(bestId);
        }

        return model.Tokenizer.Decode(decodedIds.ToArray().AsSpan(1));
    }

    private static string ModelKey(string sourceLang, string targetLang) =>
        $"{sourceLang}-{targetLang}";

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _downloadSemaphore.Dispose();
            foreach (var model in _loadedModels.Values)
            {
                model.Encoder.Dispose();
                model.Decoder.Dispose();
            }
            _loadedModels.Clear();
            _disposed = true;
        }
    }
}

internal sealed record LoadedTranslationModel(
    InferenceSession Encoder,
    InferenceSession Decoder,
    MarianTokenizer Tokenizer,
    MarianConfig Config);
