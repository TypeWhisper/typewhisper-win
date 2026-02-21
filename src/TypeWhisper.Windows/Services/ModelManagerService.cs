using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.Services.Providers;

namespace TypeWhisper.Windows.Services;

public sealed class ModelManagerService : INotifyPropertyChanged, IDisposable
{
    private readonly Dictionary<string, LocalProviderBase> _localProviders;
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, ModelStatus> _modelStatuses = new();
    private string? _activeModelId;
    private string? _loadedLocalModelId;
    private readonly HttpClient _httpClient = new();
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ActiveModelId
    {
        get => _activeModelId;
        private set { _activeModelId = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<LocalProviderBase> LocalProviders => [.. _localProviders.Values];
    public PluginManager PluginManager => _pluginManager;

    /// <summary>
    /// Checks whether a model ID refers to a plugin-provided model.
    /// Plugin model IDs use the format "plugin:{pluginId}:{modelId}".
    /// </summary>
    public static bool IsPluginModel(string modelId) => modelId.StartsWith("plugin:");

    /// <summary>
    /// Parses a plugin model ID into its components.
    /// </summary>
    public static (string PluginId, string ModelId) ParsePluginModelId(string modelId)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Not a plugin model ID: {modelId}");

        // Format: "plugin:{pluginId}:{modelId}"
        var firstColon = modelId.IndexOf(':');
        var secondColon = modelId.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
            throw new ArgumentException($"Invalid plugin model ID format: {modelId}");

        return (modelId[(firstColon + 1)..secondColon], modelId[(secondColon + 1)..]);
    }

    /// <summary>
    /// Builds a full plugin model ID from its components.
    /// </summary>
    public static string GetPluginModelId(string pluginId, string modelId) =>
        $"plugin:{pluginId}:{modelId}";

    public ITranscriptionEngine Engine
    {
        get
        {
            if (_activeModelId is not null && IsPluginModel(_activeModelId))
            {
                var (pluginId, _) = ParsePluginModelId(_activeModelId);
                var plugin = _pluginManager.TranscriptionEngines
                    .FirstOrDefault(e => e.PluginId == pluginId);
                if (plugin is not null)
                    return new PluginTranscriptionEngineAdapter(plugin);
            }

            if (_loadedLocalModelId is not null && _localProviders.TryGetValue(_loadedLocalModelId, out var local))
                return local;

            return _localProviders.Values.First();
        }
    }

    public ModelManagerService(
        IEnumerable<LocalProviderBase> localProviders,
        PluginManager pluginManager,
        ISettingsService settings)
    {
        _localProviders = localProviders.ToDictionary(p => p.Id);
        _pluginManager = pluginManager;
        _settings = settings;

        foreach (var p in _localProviders.Values)
            _modelStatuses[p.Id] = ModelStatus.NotDownloaded;
    }

    public ModelStatus GetStatus(string modelId)
    {
        if (IsPluginModel(modelId))
        {
            if (_activeModelId == modelId) return ModelStatus.Ready;
            var (pluginId, _) = ParsePluginModelId(modelId);
            var plugin = _pluginManager.TranscriptionEngines
                .FirstOrDefault(e => e.PluginId == pluginId);
            return plugin is { IsConfigured: true } ? ModelStatus.Ready : ModelStatus.NotDownloaded;
        }
        return _modelStatuses.GetValueOrDefault(modelId, ModelStatus.NotDownloaded);
    }

    public bool IsDownloaded(string modelId)
    {
        if (IsPluginModel(modelId)) return true;
        return _localProviders.TryGetValue(modelId, out var provider) && provider.IsDownloaded;
    }

    public async Task DownloadAndLoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var provider = _localProviders.GetValueOrDefault(modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");

        if (!provider.IsDownloaded)
            await DownloadModelFilesAsync(provider, cancellationToken);

        await LoadModelAsync(modelId, cancellationToken);
    }

    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (IsPluginModel(modelId))
        {
            var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
            var plugin = _pluginManager.TranscriptionEngines
                .FirstOrDefault(e => e.PluginId == pluginId)
                ?? throw new ArgumentException($"Unknown plugin: {pluginId}");
            if (!plugin.IsConfigured)
                throw new InvalidOperationException($"Kein API-Key für {plugin.ProviderDisplayName}");
            plugin.SelectModel(pluginModelId);
            ActiveModelId = modelId;
            return;
        }

        var localProvider = _localProviders.GetValueOrDefault(modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");

        if (_loadedLocalModelId == modelId && localProvider.IsModelLoaded)
        {
            ActiveModelId = modelId;
            SetStatus(modelId, ModelStatus.Ready);
            return;
        }

        if (!localProvider.IsDownloaded)
            throw new FileNotFoundException($"Model files not found for: {modelId}");

        // Unload all local providers and force native memory cleanup
        foreach (var p in _localProviders.Values)
            p.UnloadModel();
        _loadedLocalModelId = null;

        if (ActiveModelId is not null)
        {
            ActiveModelId = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        SetStatus(modelId, ModelStatus.LoadingModel);
        try
        {
            var dir = localProvider.GetModelDirectory();
            await localProvider.LoadModelAsync(dir, cancellationToken);

            SetStatus(modelId, ModelStatus.Ready);
            _loadedLocalModelId = modelId;
            ActiveModelId = modelId;
        }
        catch (Exception ex)
        {
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    public void UnloadModel()
    {
        if (ActiveModelId is not null)
        {
            foreach (var p in _localProviders.Values)
                p.UnloadModel();
            _loadedLocalModelId = null;
            SetStatus(ActiveModelId, ModelStatus.NotDownloaded);
            ActiveModelId = null;
        }
    }

    public void DeleteModel(string modelId)
    {
        if (ActiveModelId == modelId)
            UnloadModel();

        var provider = _localProviders.GetValueOrDefault(modelId);
        if (provider is null) return;

        var dir = provider.GetModelDirectory();
        foreach (var file in provider.Model.Files)
        {
            var path = Path.Combine(dir, file.FileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        if (provider.Model.SubDirectory is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);

        SetStatus(modelId, ModelStatus.NotDownloaded);
    }

    private async Task DownloadModelFilesAsync(LocalProviderBase provider, CancellationToken cancellationToken)
    {
        var dir = provider.GetModelDirectory();
        Directory.CreateDirectory(dir);

        var totalBytes = provider.Model.Files.Sum(f => (long)f.EstimatedSizeMB * 1024 * 1024);
        long cumulativeBytesRead = 0;

        foreach (var file in provider.Model.Files)
        {
            var filePath = Path.Combine(dir, file.FileName);
            if (File.Exists(filePath)) continue;

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var buffer = new byte[81920];
            long fileBytesRead = 0;
            var lastReport = DateTime.UtcNow;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var fileStream = new FileStream(filePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                int read;
                while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    fileBytesRead += read;

                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds > 250 && totalBytes > 0)
                    {
                        var progress = (double)(cumulativeBytesRead + fileBytesRead) / totalBytes;
                        SetStatus(provider.Id, ModelStatus.DownloadingModel(progress));
                        lastReport = now;
                    }
                }
            }

            File.Move(filePath + ".tmp", filePath, overwrite: true);
            cumulativeBytesRead += fileBytesRead;
        }
    }

    private void SetStatus(string modelId, ModelStatus status)
    {
        _modelStatuses[modelId] = status;
        OnPropertyChanged(nameof(GetStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            foreach (var p in _localProviders.Values)
                p.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Adapts a plugin transcription engine to the ITranscriptionEngine interface
/// used by the rest of the application.
/// </summary>
internal sealed class PluginTranscriptionEngineAdapter : ITranscriptionEngine
{
    private readonly ITranscriptionEnginePlugin _plugin;

    public PluginTranscriptionEngineAdapter(ITranscriptionEnginePlugin plugin) => _plugin = plugin;

    public bool IsModelLoaded => _plugin.IsConfigured && _plugin.SelectedModelId is not null;

    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void UnloadModel() { }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples, string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        var wavBytes = WavEncoder.Encode(audioSamples);
        var translate = task == TranscriptionTask.Translate;
        var result = await _plugin.TranscribeAsync(wavBytes, language, translate, null, cancellationToken);
        return new TranscriptionResult
        {
            Text = result.Text,
            DetectedLanguage = result.DetectedLanguage,
            Duration = result.DurationSeconds
        };
    }
}
