using System.Diagnostics;
using System.IO;
using System.Net.Http;

using LLama;
using LLama.Common;
using LLama.Sampling;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GemmaLocal;

/// <summary>
/// Provides gemma local plugin behavior.
/// </summary>
public sealed partial class GemmaLocalPlugin : ILlmProviderPlugin, ILocalLlmModelManagement
{
    private static readonly IReadOnlyList<GemmaModelDefinition> DefaultModels =
    [
        new("gemma3-4b-q4", "Gemma 3 4B (Q4_K_M)", "~3 GB", 3000, true,
            "https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/5a3566e716d80f709ed7b79817eaf7733d2a1fce/gemma-3-4b-it-Q4_K_M.gguf",
            "gemma-3-4b-it-Q4_K_M.gguf", 2489894016, "04a43a22e8d2003deda5acc262f68ec1005fa76c735a9962a8c77042a74a7d19"),
        new("gemma3-12b-q4", "Gemma 3 12B (Q4_K_M)", "~8 GB", 8000, false,
            "https://huggingface.co/unsloth/gemma-3-12b-it-GGUF/resolve/d15e4c7dc21dc55d56bf8549db57a71ad8a2a35d/gemma-3-12b-it-Q4_K_M.gguf",
            "gemma-3-12b-it-Q4_K_M.gguf", 7300778336, "15b8fd9d8672cd4240c178c217ca781409291f34e353d2e913b29c7602ceb3ff"),
        new("gemma3-27b-q4", "Gemma 3 27B (Q4_K_M)", "~17 GB", 17000, false,
            "https://huggingface.co/unsloth/gemma-3-27b-it-GGUF/resolve/7cd0121f2530b00e42c4df952d4cad4418c0b3c1/gemma-3-27b-it-Q4_K_M.gguf",
            "gemma-3-27b-it-Q4_K_M.gguf", 16546688736, "f1b699659942c777bd3ec0bcb527d6ebf34ae14ca76e3af103d58d0c9cbdadee"),
    ];

    private readonly IReadOnlyList<GemmaModelDefinition> Models;
    /// <summary>Creates the local Gemma provider with the pinned model catalog.</summary>
    public GemmaLocalPlugin() : this(DefaultModels) { }
    internal GemmaLocalPlugin(IReadOnlyList<GemmaModelDefinition> models) => Models = models;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _selectedModelId;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private string? _loadedModelId;
    private CancellationTokenSource? _verificationCancellation;
    private Task _verificationTask = Task.CompletedTask;
    internal Task CacheVerification => _verificationTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime Modified)> _verifiedModels = new();

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.gemma-local";
    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public string PluginName => "Gemma 3 (Local)";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.2.3";

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
    public string ProviderName => "Gemma 3 (Local)";
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

                // Build Gemma chat prompt
                var prompt = FormatGemmaPrompt(systemPrompt, userText);
                var promptTokenCount = _context.Tokenize(prompt, addBos: true, special: true).Length;
                var maxOutputTokens = RequireOutputBudget(
                    LlmOutputTokenBudget.Calculate(systemPrompt, userText), promptTokenCount, checked((int)_context.ContextSize));

                var executor = new StatelessExecutor(_weights, _context.Params);
                var inferenceParams = new InferenceParams
                {
                    MaxTokens = maxOutputTokens,
                    AntiPrompts = ["<end_of_turn>", "<eos>"],
                    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f },
                };

                var result = new System.Text.StringBuilder();
                await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    result.Append(token);
                }

                ct.ThrowIfCancellationRequested();
                return result.ToString().Trim();
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
    internal IReadOnlyList<GemmaModelDefinition> ModelDefinitions => Models;

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
            var pending = filePath + ".download";
            try
            {
                await ModelFileDownloader.DownloadAsync(_httpClient, model.DownloadUrl, pending, new DownloadProgress(progress), ct);
                await VerifyModelFileAsync(pending, model.SizeBytes, model.Sha256, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(pending, filePath, overwrite: true);
                RememberVerified(model, filePath);
                progress?.Report(1);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            _host?.NotifyCapabilitiesChanged();
        }
        finally { _inferenceLock.Release(); StartCacheVerification(); }
    }

    private async Task VerifyCachedModelAsync(GemmaModelDefinition model, string path, CancellationToken ct)
    {
        _verifiedModels.TryRemove(model.Id, out _);
        var before = new FileInfo(path);
        var stamp = (before.Length, before.LastWriteTimeUtc);
        await VerifyModelFileAsync(path, model.SizeBytes, model.Sha256, ct);
        var after = new FileInfo(path);
        if ((after.Length, after.LastWriteTimeUtc) != stamp) throw new IOException("The model changed during verification. Retry the download.");
        _verifiedModels[model.Id] = stamp;
    }

    private void RememberVerified(GemmaModelDefinition model, string path)
    {
        var file = new FileInfo(path);
        _verifiedModels[model.Id] = (file.Length, file.LastWriteTimeUtc);
    }

    internal static int RequireOutputBudget(int requested, int promptTokens, int contextSize)
    {
        if (LlmOutputTokenBudget.FitToContext(requested, promptTokens, contextSize, "Gemma 3") < requested)
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
                    ContextSize = 4096,
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
        _loadedModelId = null;
    }

    // Helpers

    internal static string FormatGemmaPrompt(string systemPrompt, string userText)
    {
        // Gemma 3 supports user/model turns. Instructions belong in the first user turn.
        var instructions = string.IsNullOrWhiteSpace(systemPrompt) ? "" : EscapePromptContent(systemPrompt.Trim()) + "\n\n";
        return "<start_of_turn>user\n" + instructions + EscapePromptContent(userText.Trim()) +
            "<end_of_turn>\n<start_of_turn>model\n";
    }

    // The executor parses special tokens in the full prompt. Escape content before adding
    // the trusted template so literal sentinels cannot create extra conversation turns.
    private static string EscapePromptContent(string content)
    {
        foreach (var sentinel in new[] { "<start_of_turn>", "<end_of_turn>", "<bos>", "<eos>", "<pad>" })
            content = content.Replace(sentinel, "<\u200B" + sentinel[1..], StringComparison.Ordinal);
        return content;
    }

    private string GetModelDirectory(string modelId)
    {
        var safeModelId = Path.GetFileName(modelId);
        if (string.IsNullOrWhiteSpace(safeModelId) || safeModelId is "." or "..")
            throw new ArgumentException("Model ID must not be empty.", nameof(modelId));

        return Path.Join((_host ?? throw new InvalidOperationException("Activate the plugin first.")).PluginAssetDirectory, "Models", safeModelId);
    }

    private string GetModelFilePath(string modelId, string fileName) =>
        Path.Combine(GetModelDirectory(modelId), fileName);

    private GemmaModelDefinition GetModelDefinition(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void Log(PluginLogLevel level, string message)
    {
        _host?.Log(level, message);
        Debug.WriteLine($"[GemmaLocal] {message}");
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

internal sealed record GemmaModelDefinition(
    string Id,
    string DisplayName,
    string SizeDescription,
    int EstimatedSizeMB,
    bool IsRecommended,
    string DownloadUrl,
    string FileName,
    long SizeBytes,
    string Sha256);
