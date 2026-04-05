using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using LLama;
using LLama.Common;
using LLama.Sampling;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GemmaLocal;

public sealed class GemmaLocalPlugin : ILlmProviderPlugin
{
    private static readonly IReadOnlyList<GemmaModelDefinition> Models =
    [
        new("gemma4-4b-q4", "Gemma 4 4B (Q4_K_M)", "~3 GB", 3000, true,
            "https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf",
            "gemma-3-4b-it-Q4_K_M.gguf"),
        new("gemma4-12b-q4", "Gemma 4 12B (Q4_K_M)", "~8 GB", 8000, false,
            "https://huggingface.co/unsloth/gemma-3-12b-it-GGUF/resolve/main/gemma-3-12b-it-Q4_K_M.gguf",
            "gemma-3-12b-it-Q4_K_M.gguf"),
        new("gemma4-27b-q4", "Gemma 4 27B (Q4_K_M)", "~17 GB", 17000, false,
            "https://huggingface.co/unsloth/gemma-3-27b-it-GGUF/resolve/main/gemma-3-27b-it-Q4_K_M.gguf",
            "gemma-3-27b-it-Q4_K_M.gguf"),
    ];

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _selectedModelId;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private string? _loadedModelId;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.gemma-local";
    public string PluginName => "Gemma 4 (Local)";
    public string PluginVersion => "1.0.0";

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _selectedModelId = host.GetSetting<string>("selectedModel");
        host.Log(PluginLogLevel.Info, $"Activated (model={_selectedModelId})");

        // Auto-load previously selected model in background (don't block app startup)
        if (!string.IsNullOrEmpty(_selectedModelId) && IsModelDownloaded(_selectedModelId))
        {
            var modelId = _selectedModelId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadModelAsync(modelId, CancellationToken.None);
                    host.Log(PluginLogLevel.Info, $"Auto-loaded model: {modelId}");
                }
                catch (Exception ex)
                {
                    host.Log(PluginLogLevel.Warning, $"Failed to auto-load model: {ex.Message}");
                }
            });
        }

        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        UnloadModel();
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new GemmaLocalSettingsView(this);

    // ILlmProviderPlugin

    public string ProviderName => "Gemma 4 (Local)";
    public bool IsAvailable => _loadedModelId is not null;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } = Models.Select(m =>
        new PluginModelInfo(m.Id, m.DisplayName)
        {
            SizeDescription = m.SizeDescription,
            EstimatedSizeMB = m.EstimatedSizeMB,
            IsRecommended = m.IsRecommended,
        }).ToList();

    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        await _inferenceLock.WaitAsync(ct);
        try
        {
            if (_context is null || _weights is null)
                throw new InvalidOperationException("No model loaded. Download and load a model first.");

            // Build Gemma chat prompt
            var prompt = FormatGemmaPrompt(systemPrompt, userText);

            var executor = new StatelessExecutor(_weights, _context.Params);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 2048,
                AntiPrompts = ["<end_of_turn>", "<eos>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.3f },
            };

            var result = new System.Text.StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            {
                result.Append(token);
            }

            return result.ToString().Trim();
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    // Model management (for settings view)

    internal string? SelectedModelId => _selectedModelId;
    internal string? LoadedModelId => _loadedModelId;
    internal IPluginLocalization? Loc => _host?.Localization;
    internal IReadOnlyList<GemmaModelDefinition> ModelDefinitions => Models;

    internal void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
        _host?.NotifyCapabilitiesChanged();
    }

    internal bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        var path = GetModelFilePath(modelId, model.FileName);
        return File.Exists(path);
    }

    internal async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, model.FileName);
        if (File.Exists(filePath))
        {
            progress?.Report(1.0);
            return;
        }

        Log(PluginLogLevel.Info, $"Downloading {model.DisplayName} from Hugging Face...");

        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        using var response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? model.EstimatedSizeMB * 1024L * 1024;
        long bytesRead = 0;
        var lastReport = DateTime.UtcNow;

        var buffer = new byte[81920];
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using (var fileStream = new FileStream(filePath + ".tmp", FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, true))
        {
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesRead += read;

                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds > 250)
                {
                    progress?.Report((double)bytesRead / totalBytes);
                    lastReport = now;
                }
            }
        }

        File.Move(filePath + ".tmp", filePath, overwrite: true);
        progress?.Report(1.0);
        Log(PluginLogLevel.Info, $"Download complete: {model.FileName}");
    }

    internal Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var filePath = GetModelFilePath(modelId, model.FileName);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}");

        return Task.Run(() =>
        {
            UnloadModel();

            var modelParams = new ModelParams(filePath)
            {
                ContextSize = 4096,
                GpuLayerCount = 0,  // CPU only (Backend.Cpu)
                Threads = (int)Math.Max(1, Environment.ProcessorCount / 2),
            };

            _weights = LLamaWeights.LoadFromFile(modelParams);
            _context = _weights.CreateContext(modelParams);
            _loadedModelId = modelId;
            _selectedModelId = modelId;
            _host?.SetSetting("selectedModel", modelId);
            _host?.NotifyCapabilitiesChanged();

            Log(PluginLogLevel.Info, $"Model loaded: {model.DisplayName}");
        }, ct);
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

    private static string FormatGemmaPrompt(string systemPrompt, string userText)
    {
        // Gemma 3 instruction-tuned chat format with proper system turn
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append("<start_of_turn>system\n");
            sb.Append(systemPrompt).Append('\n');
            sb.Append("IMPORTANT: Respond ONLY in the same language as the user's input. Output ONLY the requested result, nothing else. No explanations, no extra text.");
            sb.Append("<end_of_turn>\n");
        }

        sb.Append("<start_of_turn>user\n");
        sb.Append(userText);
        sb.Append("<end_of_turn>\n");
        sb.Append("<start_of_turn>model\n");
        return sb.ToString();
    }

    private string GetModelDirectory(string modelId) =>
        Path.Combine(_host?.PluginDataDirectory ?? ".", "Models", modelId);

    private string GetModelFilePath(string modelId, string fileName) =>
        Path.Combine(GetModelDirectory(modelId), fileName);

    private static GemmaModelDefinition GetModelDefinition(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void Log(PluginLogLevel level, string message)
    {
        _host?.Log(level, message);
        Debug.WriteLine($"[GemmaLocal] {message}");
    }

    public void Dispose()
    {
        UnloadModel();
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
    string FileName);
