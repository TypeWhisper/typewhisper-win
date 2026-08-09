using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.Services;

internal sealed class ModelManagerRequestException : Exception
{
    /// <summary>
    /// Gets the provider or HTTP status code associated with the result.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Performs model manager request exception.
    /// </summary>
    public ModelManagerRequestException(int statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

internal sealed record ActiveModelTranscriptionResult(
    TranscriptionResult Result,
    string? EngineId,
    string? ModelId,
    string? EngineSelectionId);

internal sealed record TranscriptionEngineIdentity(string EngineId, string ModelId);

/// <summary>
/// Provides model manager service behavior.
/// </summary>
public sealed class ModelManagerService : INotifyPropertyChanged, IDisposable
{
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;
    private readonly IDictionaryService? _dictionary;
    private readonly Dictionary<string, ModelStatus> _modelStatuses = new();
    private string? _activeModelId;
    private ITranscriptionEnginePlugin? _activeTranscriptionPlugin;
    private TranscriptionAccelerationPreference? _activeModelAccelerationPreference;
    private System.Timers.Timer? _autoUnloadTimer;
    private bool _disposed;

    /// <summary>
    /// Raised when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the active model id.
    /// </summary>
    public string? ActiveModelId
    {
        get => _activeModelId;
        private set { _activeModelId = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Gets the plugin manager.
    /// </summary>
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

    /// <summary>
    /// Gets the engine.
    /// </summary>
    public ITranscriptionEngine Engine
    {
        get
        {
            if (_activeModelId is not null && _activeTranscriptionPlugin is not null)
                return new PluginTranscriptionEngineAdapter(_activeTranscriptionPlugin, _dictionary);

            return NoOpTranscriptionEngine.Instance;
        }
    }

    /// <summary>Returns the active <see cref="ITranscriptionEnginePlugin"/> if a plugin model is selected.</summary>
    public ITranscriptionEnginePlugin? ActiveTranscriptionPlugin => _activeTranscriptionPlugin;

    internal TranscriptionEngineIdentity? ResolveTranscriptionIdentity(string? fullModelId)
    {
        if (string.IsNullOrWhiteSpace(fullModelId) || !IsPluginModel(fullModelId))
            return null;

        try
        {
            var (selectionId, modelId) = ParsePluginModelId(fullModelId);
            if (string.IsNullOrWhiteSpace(selectionId) || string.IsNullOrWhiteSpace(modelId))
                return null;

            var plugin = FindTranscriptionEngine(selectionId);
            return plugin is null
                ? null
                : new TranscriptionEngineIdentity(plugin.ProviderId, modelId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Initializes a new instance of the ModelManagerService class.
    /// </summary>
    public ModelManagerService(
        PluginManager pluginManager,
        ISettingsService settings,
        IDictionaryService? dictionary = null)
    {
        _pluginManager = pluginManager;
        _settings = settings;
        _dictionary = dictionary;
        _pluginManager.PluginStateChanged += OnPluginStateChanged;
    }

    /// <summary>
    /// Returns status.
    /// </summary>
    public ModelStatus GetStatus(string modelId)
    {
        if (_modelStatuses.TryGetValue(modelId, out var tracked))
            return tracked;

        if (!IsPluginModel(modelId))
            return ModelStatus.NotDownloaded;

        if (_activeModelId == modelId)
            return ModelStatus.Ready;

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = FindTranscriptionEngine(pluginId);

        if (plugin is null)
            return ModelStatus.NotDownloaded;

        if (plugin.SupportsModelDownload)
        {
            try
            {
                if (!TryGetPluginModelDownloadStatus(plugin, pluginModelId, modelId, out var isDownloaded))
                    return _modelStatuses[modelId];

                return isDownloaded ? ModelStatus.Ready : ModelStatus.NotDownloaded;
            }
            catch (LocalModelStorageUnavailableException ex)
            {
                return ModelStatus.Failed(ex.Message);
            }
        }

        return plugin.IsConfigured ? ModelStatus.Ready : ModelStatus.NotDownloaded;
    }

    /// <summary>
    /// Returns whether downloaded.
    /// </summary>
    public bool IsDownloaded(string modelId)
    {
        try
        {
            return IsDownloadedCore(modelId);
        }
        catch (LocalModelStorageUnavailableException)
        {
            return false;
        }
    }

    private bool IsDownloadedCore(string modelId)
    {
        if (!IsPluginModel(modelId))
            return false;

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = FindTranscriptionEngine(pluginId);

        if (plugin is null)
            return false;

        if (plugin.SupportsModelDownload)
            return TryGetPluginModelDownloadStatus(plugin, pluginModelId, modelId, out var isDownloaded)
                && isDownloaded;

        return plugin.IsConfigured;
    }

    private static bool IsDownloadedCore(ITranscriptionEnginePlugin plugin, string pluginModelId) =>
        plugin.IsModelDownloaded(pluginModelId);

    /// <summary>
    /// Downloads and load model asynchronously.
    /// </summary>
    public async Task DownloadAndLoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Unknown model: {modelId}");

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = FindTranscriptionEngine(pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        bool needsDownload;
        try
        {
            needsDownload = plugin.SupportsModelDownload && !IsDownloadedCore(plugin, pluginModelId);
        }
        catch (LocalModelStorageUnavailableException ex)
        {
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }

        if (needsDownload)
        {
            SetStatus(modelId, ModelStatus.DownloadingModel(0));
            try
            {
                plugin.SetAccelerationPreference(
                    GetAccelerationPreference(_settings.Current.LocalModelAcceleration));
                var progress = new Progress<double>(p =>
                    ReportDownloadProgress(modelId, p));
                await plugin.DownloadModelAsync(pluginModelId, progress, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetStatus(modelId, ModelStatus.NotDownloaded);
                throw;
            }
            catch (Exception ex)
            {
                SetStatus(modelId, ModelStatus.Failed(ex.Message));
                throw;
            }
        }

        await LoadModelAsync(modelId, cancellationToken);
    }

    private void ReportDownloadProgress(string modelId, double progress)
    {
        if (_modelStatuses.TryGetValue(modelId, out var current)
            && current.Type == ModelStatusType.Downloading)
        {
            SetStatus(modelId, ModelStatus.DownloadingModel(Math.Clamp(progress, 0, 1)));
        }
    }

    /// <summary>
    /// Loads the selected transcription model into memory.
    /// </summary>
    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Unknown model: {modelId}");

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = FindTranscriptionEngine(pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        if (!plugin.IsConfigured && !plugin.SupportsModelDownload)
            throw new InvalidOperationException(Loc.Instance.GetString("Error.NoApiKeyFormat", plugin.ProviderDisplayName));

        CancelAutoUnload();
        SetStatus(modelId, ModelStatus.LoadingModel);
        try
        {
            var accelerationPreference = GetAccelerationPreference(_settings.Current.LocalModelAcceleration);
            plugin.SetAccelerationPreference(accelerationPreference);

            if (plugin.SupportsModelDownload)
                await plugin.LoadModelAsync(pluginModelId, cancellationToken);

            if (!ReferenceEquals(FindTranscriptionEngine(pluginId), plugin))
                throw new InvalidOperationException("The transcription plugin changed while the model was loading.");

            plugin.SelectModel(pluginModelId);
            SetStatus(modelId, ModelStatus.Ready);
            _activeTranscriptionPlugin = plugin;
            ActiveModelId = modelId;
            _activeModelAccelerationPreference = accelerationPreference;

            if (!ReferenceEquals(FindTranscriptionEngine(pluginId), plugin))
            {
                InvalidateActiveModelState(modelId);
                throw new InvalidOperationException("The transcription plugin changed while the model was loading.");
            }
        }
        catch (Exception ex)
        {
            SetStatus(modelId, ModelStatus.Failed(GetModelLoadFailureMessage(plugin, ex)));
            throw;
        }
    }

    private static string GetModelLoadFailureMessage(ITranscriptionEnginePlugin plugin, Exception error)
    {
        var accelerationStatus = plugin.AccelerationStatus;
        if (accelerationStatus.ActiveBackend == TranscriptionAccelerationBackend.Cpu
            && accelerationStatus.DisplayText.EndsWith(
                " unavailable",
                StringComparison.OrdinalIgnoreCase))
        {
            return accelerationStatus.DisplayText;
        }

        return error.Message;
    }

    internal static TranscriptionAccelerationPreference GetAccelerationPreference(string? value) =>
        AppSettings.NormalizeLocalModelAcceleration(value) switch
        {
            AppSettings.LocalModelAccelerationCpu => TranscriptionAccelerationPreference.Cpu,
            AppSettings.LocalModelAccelerationNvidiaCuda => TranscriptionAccelerationPreference.NvidiaCuda,
            AppSettings.LocalModelAccelerationAmdVulkan => TranscriptionAccelerationPreference.AmdVulkan,
            AppSettings.LocalModelAccelerationAmdRocm => TranscriptionAccelerationPreference.AmdRocm,
            _ => TranscriptionAccelerationPreference.Auto
        };

    /// <summary>
    /// Unloads the active transcription model from memory.
    /// </summary>
    public void UnloadModel()
    {
        CancelAutoUnload();
        if (ActiveModelId is not null)
        {
            var activeModelId = ActiveModelId;
            var plugin = _activeTranscriptionPlugin;
            plugin?.UnloadModelAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"UnloadModelAsync failed: {t.Exception?.Message}");
            });
            SetStatus(activeModelId, ModelStatus.NotDownloaded);
            _activeTranscriptionPlugin = null;
            ActiveModelId = null;
            _activeModelAccelerationPreference = null;
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

    /// <summary>
    /// Returns whether the plugin can remove downloaded files for the model.
    /// </summary>
    public bool SupportsModelRemoval(string modelId)
    {
        if (!IsPluginModel(modelId))
            return false;

        try
        {
            var (pluginId, _) = ParsePluginModelId(modelId);
            return FindTranscriptionEngine(pluginId)?.SupportsModelRemoval == true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Unloads a model when necessary and removes its downloaded files.
    /// </summary>
    public async Task RemoveModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Unknown model: {modelId}", nameof(modelId));

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = FindTranscriptionEngine(pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}", nameof(modelId));

        if (!plugin.SupportsModelRemoval)
            throw new NotSupportedException($"{plugin.ProviderDisplayName} does not support model removal.");

        SetStatus(modelId, ModelStatus.RemovingModel);
        try
        {
            if (ActiveModelId == modelId)
            {
                CancelAutoUnload();
                await plugin.UnloadModelAsync();
                _activeTranscriptionPlugin = null;
                ActiveModelId = null;
                _activeModelAccelerationPreference = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(FindTranscriptionEngine(pluginId), plugin))
                throw new InvalidOperationException("The transcription plugin changed while the model was being removed.");

            await plugin.RemoveModelAsync(pluginModelId, cancellationToken);

            if (!ReferenceEquals(FindTranscriptionEngine(pluginId), plugin))
                throw new InvalidOperationException("The transcription plugin changed while the model was being removed.");

            if (IsDownloadedCore(plugin, pluginModelId))
                throw new IOException("The model plugin reported that downloaded files remain after removal.");

            SetStatus(modelId, ModelStatus.NotDownloaded);
            if (string.Equals(_settings.Current.SelectedModelId, modelId, StringComparison.Ordinal))
                _settings.Save(_settings.Current with { SelectedModelId = null });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(
                modelId,
                IsDownloadedCore(plugin, pluginModelId) ? ModelStatus.Ready : ModelStatus.NotDownloaded);
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    /// <summary>
    /// Ensures model loaded asynchronously..
    /// </summary>
    public async Task<bool> EnsureModelLoadedAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        var targetModelId = modelId ?? _settings.Current.SelectedModelId;
        if (string.IsNullOrWhiteSpace(targetModelId))
            return false;

        if (!IsPluginModel(targetModelId))
            return false;

        var (targetPluginId, _) = ParsePluginModelId(targetModelId);
        var targetPlugin = FindTranscriptionEngine(targetPluginId);
        if (targetPlugin is null)
        {
            if (ActiveModelId == targetModelId)
                InvalidateActiveModelState(targetModelId);
            return false;
        }

        var targetAccelerationPreference = GetAccelerationPreference(_settings.Current.LocalModelAcceleration);
        if (ActiveModelId == targetModelId
            && ReferenceEquals(_activeTranscriptionPlugin, targetPlugin)
            && _activeModelAccelerationPreference == targetAccelerationPreference
            && IsActiveAccelerationSatisfied(targetAccelerationPreference))
        {
            CancelAutoUnload();
            return true;
        }

        if (ActiveModelId == targetModelId)
        {
            await LoadModelAsync(targetModelId, cancellationToken);
            return true;
        }

        if (!IsDownloadedCore(targetModelId))
            return false;

        await LoadModelAsync(targetModelId, cancellationToken);
        return true;
    }

    private bool IsActiveAccelerationSatisfied(TranscriptionAccelerationPreference targetPreference)
    {
        var status = ActiveTranscriptionPlugin?.AccelerationStatus;
        if (status is null)
            return true;

        if (status.RequiresRestart)
            return false;

        return targetPreference switch
        {
            TranscriptionAccelerationPreference.Cpu => status.ActiveBackend == TranscriptionAccelerationBackend.Cpu,
            TranscriptionAccelerationPreference.NvidiaCuda => status.ActiveBackend == TranscriptionAccelerationBackend.NvidiaCuda,
            TranscriptionAccelerationPreference.AmdVulkan => status.ActiveBackend == TranscriptionAccelerationBackend.AmdVulkan,
            TranscriptionAccelerationPreference.AmdRocm => status.ActiveBackend == TranscriptionAccelerationBackend.AmdRocm,
            _ => true
        };
    }

    internal async Task<TranscriptionRequestModelScope> BeginTranscriptionRequestAsync(
        string? engineOverride,
        string? modelOverride,
        bool awaitDownload,
        CancellationToken cancellationToken = default)
    {
        var previousActiveModelId = ActiveModelId;
        var hasOverride = !string.IsNullOrWhiteSpace(engineOverride)
            || !string.IsNullOrWhiteSpace(modelOverride);

        try
        {
            if (!hasOverride)
            {
                var targetModelId = _settings.Current.SelectedModelId;
                if (string.IsNullOrWhiteSpace(targetModelId))
                    throw new ModelManagerRequestException(503, "No model loaded");

                if (awaitDownload && IsPluginModel(targetModelId))
                {
                    var (pluginId, pluginModelId) = ParsePluginModelId(targetModelId);
                    var plugin = FindTranscriptionEngine(pluginId);

                    if (plugin?.SupportsModelDownload == true && !IsDownloadedCore(plugin, pluginModelId))
                    {
                        await DownloadAndLoadModelAsync(targetModelId, cancellationToken);
                        return new TranscriptionRequestModelScope(this, previousActiveModelId, restore: false);
                    }
                }

                if (!await EnsureModelLoadedAsync(targetModelId, cancellationToken))
                    throw new ModelManagerRequestException(503, "No model loaded");

                return new TranscriptionRequestModelScope(this, previousActiveModelId, restore: false);
            }

            var resolved = ResolveRequestModel(engineOverride, modelOverride, awaitDownload);
            var fullModelId = GetPluginModelId(resolved.Plugin.GetTranscriptionSelectionId(), resolved.ModelId);

            if (resolved.Plugin.SupportsModelDownload
                && !IsDownloadedCore(resolved.Plugin, resolved.ModelId)
                && awaitDownload)
            {
                await DownloadAndLoadModelAsync(fullModelId, cancellationToken);
            }
            else
            {
                if (!await EnsureModelLoadedAsync(fullModelId, cancellationToken))
                    throw new ModelManagerRequestException(503, $"Model '{resolved.ModelId}' could not be loaded");
            }

            return new TranscriptionRequestModelScope(this, previousActiveModelId, restore: true);
        }
        catch (LocalModelStorageUnavailableException ex)
        {
            throw new ModelManagerRequestException(503, ex.Message, ex);
        }
    }

    internal async Task<ActiveModelTranscriptionResult> TranscribeActiveAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        string? prompt = null,
        CancellationToken cancellationToken = default) =>
        await TranscribeActiveWithLanguageHintsAsync(
            audioSamples,
            string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? []
                : [language],
            task,
            prompt,
            cancellationToken);

    internal async Task<ActiveModelTranscriptionResult> TranscribeActiveWithLanguageHintsAsync(
        float[] audioSamples,
        IReadOnlyList<string> languageHints,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        var plugin = ActiveTranscriptionPlugin
            ?? throw new InvalidOperationException(Loc.Instance["Error.NoActiveTranscriptionEngine"]);

        var modelId = ActiveModelId is { } activeModelId && IsPluginModel(activeModelId)
            ? ParsePluginModelId(activeModelId).ModelId
            : plugin.SelectedModelId;

        var wavBytes = WavEncoder.Encode(audioSamples);
        var translate = task == TranscriptionTask.Translate;
        var stopwatch = Stopwatch.StartNew();
        var result = await plugin.TranscribeWithLanguageHintsAsync(
            wavBytes, languageHints, translate, prompt, cancellationToken);
        stopwatch.Stop();

        var transcription = new TranscriptionResult
        {
            Text = result.Text,
            DetectedLanguage = result.DetectedLanguage,
            Duration = result.DurationSeconds,
            ProcessingTime = stopwatch.Elapsed.TotalSeconds,
            NoSpeechProbability = result.NoSpeechProbability,
            Segments = result.Segments.Select(seg => new TranscriptionSegment(seg.Text, seg.Start, seg.End)).ToList()
        };

        return new ActiveModelTranscriptionResult(
            transcription,
            plugin.ProviderId,
            modelId,
            plugin.GetTranscriptionSelectionId());
    }

    private RequestModel ResolveRequestModel(string? engineOverride, string? modelOverride, bool awaitDownload)
    {
        var engines = _pluginManager.TranscriptionEngines;
        var engine = string.IsNullOrWhiteSpace(engineOverride)
            ? null
            : FindTranscriptionEngine(engineOverride)
              ?? engines.FirstOrDefault(e => e.ProviderId.Equals(engineOverride, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(engineOverride) && engine is null)
            throw new ModelManagerRequestException(400, $"Unknown engine '{engineOverride}'");

        if (engine is null && !string.IsNullOrWhiteSpace(modelOverride))
        {
            var matches = engines
                .Where(e => e.TranscriptionModels.Any(m => m.Id.Equals(modelOverride, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
                throw new ModelManagerRequestException(400, $"Unknown model '{modelOverride}'");

            if (matches.Count > 1)
            {
                var engineIds = string.Join(", ", matches.Select(e => e.ProviderId));
                throw new ModelManagerRequestException(
                    400,
                    $"Ambiguous model id '{modelOverride}' -- matches engines: {engineIds}. Specify 'engine' too.");
            }

            engine = matches[0];
        }

        if (engine is null)
            throw new ModelManagerRequestException(503, "No engine selected");

        var modelId = string.IsNullOrWhiteSpace(modelOverride)
            ? engine.SelectedModelId ?? engine.TranscriptionModels.FirstOrDefault()?.Id
            : engine.TranscriptionModels.FirstOrDefault(m => m.Id.Equals(modelOverride, StringComparison.OrdinalIgnoreCase))?.Id
                ?? modelOverride;

        if (string.IsNullOrWhiteSpace(modelId))
            throw new ModelManagerRequestException(400, $"Engine '{engine.ProviderId}' has no available models");

        if (engine.TranscriptionModels.Count > 0
            && !engine.TranscriptionModels.Any(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ModelManagerRequestException(
                400,
                $"Model '{modelId}' is not offered by engine '{engine.ProviderId}'");
        }

        try
        {
            if (engine.SupportsModelDownload && !IsDownloadedCore(engine, modelId) && !awaitDownload)
            {
                throw new ModelManagerRequestException(
                    409,
                    $"Engine '{engine.ProviderId}' is not configured (missing API key or downloaded weights). Pass ?await_download=1 to wait for restore.");
            }
        }
        catch (LocalModelStorageUnavailableException ex)
        {
            throw new ModelManagerRequestException(503, ex.Message, ex);
        }

        if (!engine.SupportsModelDownload && !engine.IsConfigured)
        {
            throw new ModelManagerRequestException(
                409,
                $"Engine '{engine.ProviderId}' is not configured (missing API key or downloaded weights).");
        }

        return new RequestModel(engine, modelId);
    }

    private ITranscriptionEnginePlugin? FindTranscriptionEngine(string selectionId) =>
        _pluginManager.TranscriptionEngines.FirstOrDefault(engine =>
            string.Equals(engine.GetTranscriptionSelectionId(), selectionId, StringComparison.OrdinalIgnoreCase));

    private void OnPluginStateChanged(object? sender, EventArgs e)
    {
        if (ActiveModelId is null
            || _activeTranscriptionPlugin is null
            || !IsPluginModel(ActiveModelId))
        {
            return;
        }

        var (selectionId, _) = ParsePluginModelId(ActiveModelId);
        var currentPlugin = FindTranscriptionEngine(selectionId);
        if (ReferenceEquals(currentPlugin, _activeTranscriptionPlugin))
            return;

        InvalidateActiveModelState(ActiveModelId);
    }

    private void InvalidateActiveModelState(string modelId)
    {
        CancelAutoUnload();
        _activeTranscriptionPlugin = null;
        _activeModelAccelerationPreference = null;
        _modelStatuses.Remove(modelId);
        ActiveModelId = null;
        OnPropertyChanged(nameof(GetStatus));
    }

    private Task RestoreRequestModelAsync(string? previousActiveModelId)
    {
        if (previousActiveModelId is null)
        {
            ActiveModelId = null;
            _activeModelAccelerationPreference = null;
            return Task.CompletedTask;
        }

        if (ActiveModelId == previousActiveModelId)
            return Task.CompletedTask;

        return LoadModelAsync(previousActiveModelId);
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
        if (_modelStatuses.TryGetValue(modelId, out var current) && current == status)
            return;

        _modelStatuses[modelId] = status;
        OnPropertyChanged(nameof(GetStatus));
    }

    private bool TryGetPluginModelDownloadStatus(
        ITranscriptionEnginePlugin plugin,
        string pluginModelId,
        string fullModelId,
        out bool isDownloaded)
    {
        try
        {
            isDownloaded = plugin.IsModelDownloaded(pluginModelId);
            return true;
        }
        catch (Exception ex) when (IsRecoverablePluginStatusException(ex))
        {
            isDownloaded = false;
            var message = string.IsNullOrWhiteSpace(ex.Message)
                ? ex.GetType().Name
                : ex.Message;
            Debug.WriteLine($"Plugin '{plugin.PluginId}' failed while checking model status: {message}");
            SetStatus(fullModelId, ModelStatus.Failed(message));
            return false;
        }
    }

    private static bool IsRecoverablePluginStatusException(Exception ex) =>
        ex is not LocalModelStorageUnavailableException
            and not OutOfMemoryException
            and not AppDomainUnloadedException
            and not BadImageFormatException;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            CancelAutoUnload();
            _pluginManager.PluginStateChanged -= OnPluginStateChanged;
            _disposed = true;
        }
    }

    private sealed record RequestModel(ITranscriptionEnginePlugin Plugin, string ModelId);

    internal sealed class TranscriptionRequestModelScope : IAsyncDisposable
    {
        private readonly ModelManagerService _owner;
        private readonly string? _previousActiveModelId;
        private readonly bool _restore;
        private bool _disposed;

        internal TranscriptionRequestModelScope(
            ModelManagerService owner,
            string? previousActiveModelId,
            bool restore)
        {
            _owner = owner;
            _previousActiveModelId = previousActiveModelId;
            _restore = restore;
        }

        /// <summary>
        /// Releases asynchronous resources owned by this session.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!_restore)
                return;

            try
            {
                await _owner.RestoreRequestModelAsync(_previousActiveModelId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore API request model: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Provides no-op transcription engine behavior.
/// </summary>
internal sealed class NoOpTranscriptionEngine : ITranscriptionEngine
{
    /// <summary>
    /// Gets the shared no-op engine instance.
    /// </summary>
    public static readonly NoOpTranscriptionEngine Instance = new();

    /// <summary>
    /// Gets whether a transcription model is currently loaded.
    /// </summary>
    public bool IsModelLoaded => false;

    /// <summary>
    /// Loads the selected transcription model into memory.
    /// </summary>
    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Unloads the active transcription model from memory.
    /// </summary>
    public void UnloadModel() { }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples, string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionResult { Text = string.Empty });
}

/// <summary>
/// Provides plugin transcription engine adapter behavior.
/// </summary>
internal sealed class PluginTranscriptionEngineAdapter : ITranscriptionEngine
{
    private readonly ITranscriptionEnginePlugin _plugin;
    private readonly IDictionaryService? _dictionary;

    /// <summary>
    /// Performs plugin transcription engine adapter.
    /// </summary>
    public PluginTranscriptionEngineAdapter(
        ITranscriptionEnginePlugin plugin,
        IDictionaryService? dictionary = null)
    {
        _plugin = plugin;
        _dictionary = dictionary;
    }

    /// <summary>
    /// Gets whether a transcription model is currently loaded.
    /// </summary>
    public bool IsModelLoaded => _plugin.IsConfigured && _plugin.SelectedModelId is not null;

    /// <summary>
    /// Loads the selected transcription model into memory.
    /// </summary>
    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Unloads the active transcription model from memory.
    /// </summary>
    public void UnloadModel() { }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples, string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        var wavBytes = WavEncoder.Encode(audioSamples);
        var translate = task == TranscriptionTask.Translate && _plugin.SupportsTranslation;
        var result = await _plugin.TranscribeAsync(
            wavBytes,
            language,
            translate,
            GetDictionaryPrompt(),
            cancellationToken);
        return new TranscriptionResult
        {
            Text = result.Text,
            DetectedLanguage = result.DetectedLanguage,
            Duration = result.DurationSeconds,
            NoSpeechProbability = result.NoSpeechProbability,
            Segments = result.Segments.Select(seg => new TranscriptionSegment(seg.Text, seg.Start, seg.End)).ToList()
        };
    }

    /// <summary>
    /// Transcribes PCM audio using ordered language hints.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeWithLanguageHintsAsync(
        float[] audioSamples,
        IReadOnlyList<string> languageHints,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        var wavBytes = WavEncoder.Encode(audioSamples);
        var translate = task == TranscriptionTask.Translate && _plugin.SupportsTranslation;
        var result = await _plugin.TranscribeWithLanguageHintsAsync(
            wavBytes, languageHints, translate, GetDictionaryPrompt(), cancellationToken);
        return new TranscriptionResult
        {
            Text = result.Text,
            DetectedLanguage = result.DetectedLanguage,
            Duration = result.DurationSeconds,
            NoSpeechProbability = result.NoSpeechProbability,
            Segments = result.Segments.Select(seg => new TranscriptionSegment(seg.Text, seg.Start, seg.End)).ToList()
        };
    }

    private string? GetDictionaryPrompt() =>
        TranscriptionDictionaryPrompt.Create(_dictionary, _plugin);
}
