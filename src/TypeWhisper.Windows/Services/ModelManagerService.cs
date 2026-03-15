using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.Services;

public sealed class ModelManagerService : INotifyPropertyChanged, IDisposable
{
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, ModelStatus> _modelStatuses = new();
    private string? _activeModelId;
    private System.Timers.Timer? _autoUnloadTimer;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ActiveModelId
    {
        get => _activeModelId;
        private set { _activeModelId = value; OnPropertyChanged(); }
    }

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

            // Fallback: first available transcription engine
            var fallback = _pluginManager.TranscriptionEngines.FirstOrDefault();
            if (fallback is not null)
                return new PluginTranscriptionEngineAdapter(fallback);

            return NoOpTranscriptionEngine.Instance;
        }
    }

    /// <summary>Returns the active <see cref="ITranscriptionEnginePlugin"/> if a plugin model is selected.</summary>
    public ITranscriptionEnginePlugin? ActiveTranscriptionPlugin
    {
        get
        {
            if (_activeModelId is null || !IsPluginModel(_activeModelId))
                return null;
            var (pluginId, _) = ParsePluginModelId(_activeModelId);
            return _pluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == pluginId);
        }
    }

    public ModelManagerService(PluginManager pluginManager, ISettingsService settings)
    {
        _pluginManager = pluginManager;
        _settings = settings;
    }

    public ModelStatus GetStatus(string modelId)
    {
        if (_modelStatuses.TryGetValue(modelId, out var tracked))
            return tracked;

        if (!IsPluginModel(modelId))
            return ModelStatus.NotDownloaded;

        if (_activeModelId == modelId)
            return ModelStatus.Ready;

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId);

        if (plugin is null)
            return ModelStatus.NotDownloaded;

        if (plugin.SupportsModelDownload)
            return plugin.IsModelDownloaded(pluginModelId) ? ModelStatus.Ready : ModelStatus.NotDownloaded;

        return plugin.IsConfigured ? ModelStatus.Ready : ModelStatus.NotDownloaded;
    }

    public bool IsDownloaded(string modelId)
    {
        if (!IsPluginModel(modelId))
            return false;

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId);

        if (plugin is null)
            return false;

        if (plugin.SupportsModelDownload)
            return plugin.IsModelDownloaded(pluginModelId);

        return plugin.IsConfigured;
    }

    public async Task DownloadAndLoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Unknown model: {modelId}");

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        if (plugin.SupportsModelDownload && !plugin.IsModelDownloaded(pluginModelId))
        {
            SetStatus(modelId, ModelStatus.DownloadingModel(0));

            var progress = new Progress<double>(p => SetStatus(modelId, ModelStatus.DownloadingModel(p)));
            await plugin.DownloadModelAsync(pluginModelId, progress, cancellationToken);
        }

        await LoadModelAsync(modelId, cancellationToken);
    }

    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Unknown model: {modelId}");

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        if (!plugin.IsConfigured && !plugin.SupportsModelDownload)
            throw new InvalidOperationException(Loc.Instance.GetString("Error.NoApiKeyFormat", plugin.ProviderDisplayName));

        CancelAutoUnload();
        SetStatus(modelId, ModelStatus.LoadingModel);
        try
        {
            if (plugin.SupportsModelDownload)
                await plugin.LoadModelAsync(pluginModelId, cancellationToken);

            plugin.SelectModel(pluginModelId);
            SetStatus(modelId, ModelStatus.Ready);
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
        CancelAutoUnload();
        if (ActiveModelId is not null)
        {
            var plugin = ActiveTranscriptionPlugin;
            plugin?.UnloadModelAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"UnloadModelAsync failed: {t.Exception?.Message}");
            });
            SetStatus(ActiveModelId, ModelStatus.NotDownloaded);
            ActiveModelId = null;
        }
    }

    /// <summary>
    /// Schedules auto-unload after the configured idle timeout.
    /// Call this after every transcription completes.
    /// </summary>
    public void ScheduleAutoUnload()
    {
        CancelAutoUnload();

        var seconds = _settings.Current.ModelAutoUnloadSeconds;
        if (seconds <= 0 || ActiveModelId is null)
            return;

        _autoUnloadTimer = new System.Timers.Timer(seconds * 1000.0);
        _autoUnloadTimer.AutoReset = false;
        _autoUnloadTimer.Elapsed += (_, _) =>
        {
            System.Diagnostics.Debug.WriteLine($"Auto-unloading model after {seconds}s idle");
            UnloadModel();
        };
        _autoUnloadTimer.Start();
    }

    private void CancelAutoUnload()
    {
        _autoUnloadTimer?.Stop();
        _autoUnloadTimer?.Dispose();
        _autoUnloadTimer = null;
    }

    public void DeleteModel(string modelId)
    {
        if (ActiveModelId == modelId)
            UnloadModel();

        SetStatus(modelId, ModelStatus.NotDownloaded);
    }

    /// <summary>
    /// Migrates old local model IDs to plugin-prefixed IDs.
    /// Call on startup before loading models.
    /// </summary>
    public void MigrateSettings()
    {
        var current = _settings.Current;
        var changed = false;

        var migratedModelId = MigrateModelId(current.SelectedModelId);
        if (migratedModelId != current.SelectedModelId)
        {
            current = current with { SelectedModelId = migratedModelId };
            changed = true;
        }

        if (changed)
            _settings.Save(current);
    }

    /// <summary>
    /// Migrates a legacy local model ID to the new plugin-prefixed format.
    /// Returns the input unchanged if no migration is needed.
    /// </summary>
    public static string? MigrateModelId(string? modelId) => modelId switch
    {
        "parakeet-tdt-0.6b" => GetPluginModelId("com.typewhisper.sherpa-onnx", "parakeet-tdt-0.6b"),
        "canary-1b-flash" or "canary-180m-flash" => GetPluginModelId("com.typewhisper.sherpa-onnx", "canary-180m-flash"),
        _ => modelId
    };

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
            CancelAutoUnload();
            _disposed = true;
        }
    }
}

/// <summary>
/// No-op transcription engine returned when no plugin provides a real engine.
/// Prevents null-reference / InvalidOperationException in callers.
/// </summary>
internal sealed class NoOpTranscriptionEngine : ITranscriptionEngine
{
    public static readonly NoOpTranscriptionEngine Instance = new();

    public bool IsModelLoaded => false;

    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void UnloadModel() { }

    public Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples, string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionResult { Text = string.Empty });
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
