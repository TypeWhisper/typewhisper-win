using System.Diagnostics;
using System.IO;
using System.Net.Http;

using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.LocalLlm;

/// <summary>
/// Runs Gemma 4, Qwen3.5 and LFM2.5 GGUF models locally through llama.cpp.
/// </summary>
public sealed partial class LocalLlmPlugin : ILlmProviderPlugin, ILocalLlmModelManagement
{
    private const int ContextSize = 8192;

    private static readonly IReadOnlyList<LocalLlmModelDefinition> DefaultModels =
    [
        new("gemma-4-e2b-it-q4", "Gemma 4 E2B (Q4_K_M)", "~3.1 GB", 3100, true,
            "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF/resolve/0314792d7f1f7e229411f620751375812bb9faf2/gemma-4-E2B-it-Q4_K_M.gguf",
            "gemma-4-E2B-it-Q4_K_M.gguf", 3106738272, "740185b21d22ceb83a11c3aa62ad5842ef32c70f6096d756bbee85a1e4ec34b8"),
        new("gemma-4-e4b-it-q4", "Gemma 4 E4B (Q4_K_M)", "~5 GB", 5000, false,
            "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3/gemma-4-E4B-it-Q4_K_M.gguf",
            "gemma-4-E4B-it-Q4_K_M.gguf", 4977171584, "85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87"),
        new("gemma-4-e4b-it-q8", "Gemma 4 E4B (Q8_0)", "~8.2 GB", 8200, false,
            "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3/gemma-4-E4B-it-Q8_0.gguf",
            "gemma-4-E4B-it-Q8_0.gguf", 8192953472, "f8854aa4480df62585a279e7ca0a881554fc18a41c59c4f62642d16a2ae47012"),
        new("gemma-4-26b-a4b-it-q4", "Gemma 4 26B-A4B (Q4_K_M, MoE)", "~17 GB", 17000, false,
            "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-GGUF/resolve/c099eb48e663fd284577b04978a94ffccb261841/gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", 16947541728, "f2c28b3dc4776931ac6f879e11f203dec637ea0f14267a86ec8f6165f63f293f"),
        new("qwen3.5-2b-q4", "Qwen3.5 2B (Q4_K_M)", "~1.3 GB", 1300, false,
            "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q4_K_M.gguf",
            "Qwen3.5-2B-Q4_K_M.gguf", 1280835840, "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
            LocalLlmModelFamily.Qwen35),
        new("lfm2.5-2.6b-q4", "LFM2.5 2.6B (Q4_K_M)", "~1.7 GB", 1700, false,
            "https://huggingface.co/LiquidAI/LFM2.5-2.6B-GGUF/resolve/e7caca5d835a3901a8e0d63e94009429bafafdfc/LFM2.5-2.6B-Q4_K_M.gguf",
            "LFM2.5-2.6B-Q4_K_M.gguf", 1674455040, "02a8b7e17487d326e46d68ce0ba24211e1b80a14c4cd0597fa73c1cd697f52ed",
            LocalLlmModelFamily.Lfm25),
    ];

    private readonly IReadOnlyList<LocalLlmModelDefinition> Models;
    /// <summary>Creates the local LLM provider with the pinned model catalog.</summary>
    public LocalLlmPlugin() : this(DefaultModels) { }
    internal LocalLlmPlugin(IReadOnlyList<LocalLlmModelDefinition> models) => Models = models;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _selectedModelId;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private IReadOnlySet<LLamaToken> _stopTokens = new HashSet<LLamaToken>();
    private string? _loadedModelId;
    private CancellationTokenSource? _verificationCancellation;
    private Task _verificationTask = Task.CompletedTask;
    internal Task CacheVerification => _verificationTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime Modified)> _verifiedModels = new();

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.local-llm-llamacpp";
    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public string PluginName => "Local LLM (llama.cpp)";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _selectedModelId = host.GetSetting<string>("selectedModel");
        host.Log(PluginLogLevel.Info, $"Activated (model={_selectedModelId})");

        _verifiedModels.Clear();
        StartCacheVerification();
        return Task.CompletedTask;
    }

    private void StartCacheVerification()
    {
        if (_host is null || _verificationCancellation is not null) return;
        var cancellation = _verificationCancellation = new();
        _verificationTask = Task.Run(async () =>
        {
            foreach (var model in Models)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (IsModelDownloaded(model.Id)) continue;
                var path = GetModelFilePath(model.Id, model.FileName);
                if (!File.Exists(path)) continue;
                try
                {
                    await VerifyCachedModelAsync(model, path, cancellation.Token);
                    _host?.NotifyCapabilitiesChanged();
                }
                catch (IOException) { /* Keep Download available for missing or corrupt files. */ }
                catch (UnauthorizedAccessException) { /* Loading will report the access failure. */ }
            }
        }, cancellation.Token);
    }

    private async Task StopCacheVerificationAsync()
    {
        if (Interlocked.Exchange(ref _verificationCancellation, null) is not { } cancellation) return;
        cancellation.Cancel();
        try { await _verificationTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    private async Task BeginModelMutationAsync(CancellationToken ct)
    {
        await StopCacheVerificationAsync();
        try { await _inferenceLock.WaitAsync(ct); }
        catch { StartCacheVerification(); throw; }
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public async Task DeactivateAsync()
    {
        await StopCacheVerificationAsync();
        await UnloadModelAsync(CancellationToken.None);
        _host = null;
    }



    // ILlmProviderPlugin

    /// <summary>
    /// Gets the provider name.
    /// </summary>
    public string ProviderName => "Local LLM (llama.cpp)";
    /// <summary>
    /// Gets whether the provider can currently accept requests.
    /// </summary>
    public bool IsAvailable => _loadedModelId is not null;

    /// <summary>
    /// Gets the supported models.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> SupportedModels => ModelCatalog.Where(model => model.Id == _loadedModelId).ToArray();

    private IReadOnlyList<PluginModelInfo> ModelCatalog => Models.Select(m =>
        new PluginModelInfo(m.Id, m.DisplayName)
        {
            Publisher = m.Family switch { LocalLlmModelFamily.Gemma4 => "Google", LocalLlmModelFamily.Qwen35 => "Qwen", _ => "Liquid AI" },
            SizeDescription = m.SizeDescription,
            EstimatedSizeMB = m.EstimatedSizeMB,
            IsRecommended = m.IsRecommended,
        }).ToList();

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                if (model != _loadedModelId) throw new InvalidOperationException("Load the selected model in plugin settings first.");
                if (_context is null || _weights is null)
                    throw new InvalidOperationException("No model loaded. Download and load a model first.");

                var family = GetModelDefinition(model).Family;
                var promptTokens = TokenizeLoadedPrompt(systemPrompt, userText);
                var requestedTokens = LlmOutputTokenBudget.Calculate(systemPrompt, userText)
                    + (LocalLlmChatFormat.Reasons(family) ? LocalLlmChatFormat.ReasoningReserveTokens : 0);
                var maxOutputTokens = RequireOutputBudget(requestedTokens, promptTokens.Length, checked((int)_context.ContextSize));

                // LLamaSharp executors do not forward cancellation to prompt prefill.
                // Use bounded batches so cancellation waits for at most one small native decode.
                // Inference is serialized: reset the loaded context instead of allocating
                // a second native KV/cache alongside the already resident context.
                var context = _context;
                ct.ThrowIfCancellationRequested();
                context.NativeHandle.MemoryClear(true);
                using var sampling = new DefaultSamplingPipeline { Temperature = 0.1f };
                var batch = new LLamaBatch();
                var batchSize = Math.Min(32, checked((int)context.BatchSize));
                for (var offset = 0; offset < promptTokens.Length; offset += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Clear();
                    var end = Math.Min(offset + batchSize, promptTokens.Length);
                    for (var position = offset; position < end; position++)
                        batch.Add(promptTokens[position], position, LLamaSeqId.Zero, position == end - 1);
                    await DecodeBatchAsync(context, batch, ct);
                }

                // Special tokens stay visible so reasoning channels can be removed as text.
                var decoder = new StreamingTokenDecoder(context) { DecodeSpecialTokens = true };
                var result = new System.Text.StringBuilder();
                var endOfGeneration = false;
                for (var generated = 0; generated < maxOutputTokens; generated++)
                {
                    ct.ThrowIfCancellationRequested();
                    // LLamaSharp Sample calls llama_sampler_sample, which also accepts
                    // the token. Calling Accept again would update sampler history twice.
                    var token = sampling.Sample(context.NativeHandle, batch.TokenCount - 1);
                    // Stop on the turn-end token itself; the same characters typed as text are ordinary output.
                    if (token.IsEndOfGeneration(_weights.Vocab) || _stopTokens.Contains(token)) { endOfGeneration = true; break; }
                    decoder.Add(token);
                    result.Append(decoder.Read());
                    batch.Clear();
                    batch.Add(token, promptTokens.Length + generated, LLamaSeqId.Zero, true);
                    await DecodeBatchAsync(context, batch, ct);
                }

                ct.ThrowIfCancellationRequested();
                if (!endOfGeneration)
                    throw new PluginRequestException("The local model stopped the workflow response at its token limit.",
                        PluginRequestFailureKind.OutputTruncated, isTransient: false);
                return LocalLlmChatFormat.ExtractAnswer(family, result.ToString());
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    // Model management (for settings view)

    internal string? SelectedModelId => _selectedModelId;
    internal string? LoadedModelId => _loadedModelId;
    internal IPluginLocalization? Loc => PortableLocalization.TryGet(_host);
    internal IReadOnlyList<LocalLlmModelDefinition> ModelDefinitions => Models;

    internal void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        _host?.SetSetting("selectedModel", modelId);
        _selectedModelId = modelId;
        _host?.NotifyCapabilitiesChanged();
    }

    internal bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        var path = GetModelFilePath(modelId, model.FileName);
        if (!_verifiedModels.TryGetValue(modelId, out var verified)) return false;
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == verified.Size && file.LastWriteTimeUtc == verified.Modified;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <inheritdoc />
    public IReadOnlyList<LocalLlmModelState> LocalModels => ModelCatalog.Select(model =>
        new LocalLlmModelState(model, IsModelDownloaded(model.Id), _loadedModelId == model.Id)).ToArray();

    private static async Task DecodeBatchAsync(LLamaContext context, LLamaBatch batch, CancellationToken ct)
    {
        var status = await context.DecodeAsync(batch, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (status != DecodeResult.Ok) throw new LLamaDecodeError(status);
    }

    /// <inheritdoc />
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        await BeginModelMutationAsync(ct);
        try
        {
            var model = GetModelDefinition(modelId);
            var dir = GetModelDirectory(modelId);
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, model.FileName);
            if (File.Exists(filePath) && new FileInfo(filePath).Length == model.SizeBytes)
            {
                try
                {
                    await VerifyCachedModelAsync(model, filePath, ct);
                    progress?.Report(1); return;
                }
                catch (IOException)
                {
                    // Replace corrupt cached files through the same verified pending-file path.
                    ct.ThrowIfCancellationRequested();
                }
            }
            await ResumableModelDownloader.DownloadAsync(_httpClient, model, filePath, new DownloadProgress(progress), ct);
            RememberVerified(model, filePath);
            progress?.Report(1);
        }
        finally { _inferenceLock.Release(); StartCacheVerification(); _host?.NotifyCapabilitiesChanged(); }
    }

    private async Task VerifyCachedModelAsync(LocalLlmModelDefinition model, string path, CancellationToken ct)
    {
        _verifiedModels.TryRemove(model.Id, out _);
        var before = new FileInfo(path);
        var stamp = (before.Length, before.LastWriteTimeUtc);
        await VerifyModelFileAsync(path, model.SizeBytes, model.Sha256, ct);
        var after = new FileInfo(path);
        if ((after.Length, after.LastWriteTimeUtc) != stamp) throw new IOException("The model changed during verification. Retry the download.");
        _verifiedModels[model.Id] = stamp;
    }

    private void RememberVerified(LocalLlmModelDefinition model, string path)
    {
        var file = new FileInfo(path);
        _verifiedModels[model.Id] = (file.Length, file.LastWriteTimeUtc);
    }

    internal static int RequireOutputBudget(int requested, int promptTokens, int contextSize)
    {
        if (LlmOutputTokenBudget.FitToContext(requested, promptTokens, contextSize, "Local LLM") < requested)
            throw new PluginRequestException("This text is too long for the local model to return a complete result. Split it into smaller sections.",
                PluginRequestFailureKind.RequestTooLarge, isTransient: false);
        return requested;
    }

    internal static async Task VerifyModelFileAsync(string path, long size, string sha256, CancellationToken ct)
    {
        if (new FileInfo(path).Length != size) throw new IOException("The downloaded model has an unexpected size.");
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        if (!Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The downloaded model failed its integrity check. Please retry the download.");
    }

    private sealed class DownloadProgress(IProgress<double>? target) : IProgress<double>
    {
        public void Report(double value) => target?.Report(Math.Min(0.99, value));
    }

    /// <inheritdoc />
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        await BeginModelMutationAsync(ct);
        try
        {
            var model = GetModelDefinition(modelId);
            var filePath = GetModelFilePath(modelId, model.FileName);
            if (!File.Exists(filePath) || new FileInfo(filePath).Length != model.SizeBytes)
                throw new FileNotFoundException("Download the model before loading it.");
            if (_loadedModelId == modelId) return;
            await VerifyCachedModelAsync(model, filePath, ct);
            await Task.Run(() =>
            {
                var modelParams = new ModelParams(filePath)
                {
                    ContextSize = ContextSize,
                    GpuLayerCount = 0,
                    Threads = _host!.GetSetting<int?>("threads") is > 0 and var threads ? Math.Min(threads, Environment.ProcessorCount) : Math.Max(1, Environment.ProcessorCount / 2),
                };
                LLamaWeights? weights = null;
                LLamaContext? context = null;
                try
                {
                    weights = LLamaWeights.LoadFromFile(modelParams);
                    ct.ThrowIfCancellationRequested();
                    context = weights.CreateContext(modelParams);
                    ct.ThrowIfCancellationRequested();
                    _host!.SetSetting("selectedModel", modelId);
                    UnloadModel();
                    _stopTokens = ResolveStopTokens(context, model.Family);
                    _weights = weights; _context = context;
                    weights = null; context = null;
                    _loadedModelId = _selectedModelId = modelId;
                }
                finally { context?.Dispose(); weights?.Dispose(); }
            }, ct);
            _host?.NotifyCapabilitiesChanged();
        }
        finally { _inferenceLock.Release(); StartCacheVerification(); }
    }

    /// <inheritdoc />
    public async Task UnloadModelAsync(CancellationToken ct)
    {
        await _inferenceLock.WaitAsync(ct);
        try { UnloadModel(); _host?.NotifyCapabilitiesChanged(); }
        finally { _inferenceLock.Release(); }
    }

    /// <inheritdoc />
    public async Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        await BeginModelMutationAsync(ct);
        try
        {
            var model = GetModelDefinition(modelId);
            var path = GetModelFilePath(modelId, model.FileName);
            ct.ThrowIfCancellationRequested();
            if (_loadedModelId == modelId) UnloadModel();
            File.Delete(path);
            _verifiedModels.TryRemove(modelId, out _);
            _host?.NotifyCapabilitiesChanged();
        }
        finally { _inferenceLock.Release(); StartCacheVerification(); }
    }

    internal void UnloadModel()
    {
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _stopTokens = new HashSet<LLamaToken>();
        _loadedModelId = null;
    }

    // Helpers

    // Only template pieces are parsed for special tokens, so user text cannot open or close conversation turns.
    internal LLamaToken[] TokenizeLoadedPrompt(string systemPrompt, string userText)
    {
        var context = _context ?? throw new InvalidOperationException("No model loaded. Download and load a model first.");
        var segments = LocalLlmChatFormat.Format(GetModelDefinition(_loadedModelId!).Family, systemPrompt, userText);
        return segments.SelectMany((segment, index) => context.Tokenize(segment.Text, addBos: index == 0, special: segment.IsTemplate)).ToArray();
    }

    internal IReadOnlySet<LLamaToken> StopTokens => _stopTokens;

    // Some GGUF vocabularies do not flag every turn-end marker as end of generation.
    private static HashSet<LLamaToken> ResolveStopTokens(LLamaContext context, LocalLlmModelFamily family) =>
        LocalLlmChatFormat.StopMarkers(family)
            .Select(marker => context.Tokenize(marker, addBos: false, special: true))
            .Where(tokens => tokens.Length == 1)
            .Select(tokens => tokens[0])
            .ToHashSet();

    private string GetModelDirectory(string modelId)
    {
        var safeModelId = Path.GetFileName(modelId);
        if (string.IsNullOrWhiteSpace(safeModelId) || safeModelId is "." or "..")
            throw new ArgumentException("Model ID must not be empty.", nameof(modelId));

        return Path.Join((_host ?? throw new InvalidOperationException("Activate the plugin first.")).PluginAssetDirectory, "Models", safeModelId);
    }

    private string GetModelFilePath(string modelId, string fileName) =>
        Path.Combine(GetModelDirectory(modelId), fileName);

    private LocalLlmModelDefinition GetModelDefinition(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void Log(PluginLogLevel level, string message)
    {
        _host?.Log(level, message);
        Debug.WriteLine($"[LocalLlm] {message}");
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        StopCacheVerificationAsync().GetAwaiter().GetResult();
        _inferenceLock.Wait();
        try { UnloadModel(); }
        finally { _inferenceLock.Release(); }
        _inferenceLock.Dispose();
        _httpClient.Dispose();
    }
}

internal sealed record LocalLlmModelDefinition(
    string Id,
    string DisplayName,
    string SizeDescription,
    int EstimatedSizeMB,
    bool IsRecommended,
    string DownloadUrl,
    string FileName,
    long SizeBytes,
    string Sha256,
    LocalLlmModelFamily Family = LocalLlmModelFamily.Gemma4);
