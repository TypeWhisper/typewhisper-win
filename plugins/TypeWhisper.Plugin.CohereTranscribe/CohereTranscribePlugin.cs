using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CohereTranscribe;

/// <summary>
/// Provides fully local Cohere Transcribe inference on Windows through CrispASR.
/// </summary>
public sealed class CohereTranscribePlugin : ITranscriptionEnginePlugin
{
    internal const string ModelId = "cohere-transcribe-03-2026-q5_0";
    internal const string HuggingFaceTokenSecretName = "hugging-face-token";

    private static readonly IReadOnlyList<string> Languages =
        ["en", "de", "fr", "it", "es", "pt", "el", "nl", "pl", "vi", "zh", "ar", "ja", "ko"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICohereLocalAssetManager? _assets;
    private ICrispAsrServer? _server;
    private IPluginHostServices? _host;
    private string? _selectedModelId;
    private string? _loadedModelId;
    private string? _huggingFaceToken;
    private bool _disposed;
    private TranscriptionAccelerationPreference _accelerationPreference =
        TranscriptionAccelerationPreference.Auto;
    private TranscriptionAccelerationStatus _accelerationStatus = new(
        TranscriptionAccelerationBackend.Cpu,
        "Not loaded",
        "The best available local runtime will be selected when the model loads.");

    /// <summary>
    /// Initializes a new instance of the Cohere Transcribe plugin.
    /// </summary>
    public CohereTranscribePlugin()
    {
    }

    internal CohereTranscribePlugin(
        ICohereLocalAssetManager assets,
        ICrispAsrServer server)
    {
        _assets = assets;
        _server = server;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.cohere-transcribe";

    /// <summary>
    /// Gets the plugin name displayed by the host.
    /// </summary>
    public string PluginName => "Cohere Transcribe (Local)";

    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";

    /// <summary>
    /// Gets the stable transcription provider identifier.
    /// </summary>
    public string ProviderId => "cohere-transcribe";

    /// <summary>
    /// Gets the provider name displayed in the model manager.
    /// </summary>
    public string ProviderDisplayName => "Cohere Transcribe (Local)";

    /// <summary>
    /// Gets whether the local runtime platform is supported.
    /// </summary>
    public bool IsConfigured =>
        OperatingSystem.IsWindows()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Gets the available local Cohere transcription models.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [
        new PluginModelInfo(ModelId, "Cohere Transcribe 2B (Q5_0)")
        {
            SizeDescription = "~1.7 GB + local runtime",
            EstimatedSizeMB = 1_700,
            IsRecommended = true,
            LanguageCount = 14
        }
    ];

    /// <summary>
    /// Gets the currently selected model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;

    /// <summary>
    /// Gets whether speech-to-English translation is supported.
    /// </summary>
    public bool SupportsTranslation => false;

    /// <summary>
    /// Gets whether the host can download the model and runtime.
    /// </summary>
    public bool SupportsModelDownload => true;

    /// <summary>
    /// Gets the language codes supported by Cohere Transcribe.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => Languages;

    /// <summary>
    /// Gets the local acceleration backends offered on Windows.
    /// </summary>
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
    [
        TranscriptionAccelerationBackend.Cpu,
        TranscriptionAccelerationBackend.NvidiaCuda,
        TranscriptionAccelerationBackend.AmdVulkan
    ];

    /// <summary>
    /// Gets the requested acceleration preference.
    /// </summary>
    public TranscriptionAccelerationPreference AccelerationPreference => _accelerationPreference;

    /// <summary>
    /// Gets the active acceleration status.
    /// </summary>
    public TranscriptionAccelerationStatus AccelerationStatus => _accelerationStatus;

    /// <summary>
    /// Activates the plugin and prepares its isolated asset storage.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        _assets ??= new CohereLocalAssetManager(host.PluginAssetDirectory);
        _server ??= new CrispAsrServer(host.Log);
        var storedToken = await host.LoadSecretAsync(HuggingFaceTokenSecretName);
        try
        {
            _huggingFaceToken = NormalizeHuggingFaceToken(storedToken);
        }
        catch (ArgumentException)
        {
            _huggingFaceToken = null;
            host.Log(
                PluginLogLevel.Warning,
                "Ignoring and removing the stored Hugging Face token because it is malformed.");
            try
            {
                await host.DeleteSecretAsync(HuggingFaceTokenSecretName);
            }
            catch (Exception exception) when (IsExpectedSecretStorageFailure(exception))
            {
                host.Log(
                    PluginLogLevel.Warning,
                    $"The malformed Hugging Face token could not be removed: {exception.Message}");
            }
        }

        _assets.SetHuggingFaceToken(_huggingFaceToken);

        var persistedModel = host.GetSetting<string>("selectedModel");
        _selectedModelId = string.Equals(persistedModel, ModelId, StringComparison.Ordinal)
            ? persistedModel
            : ModelId;

        host.Log(
            PluginLogLevel.Info,
            $"Activated local Cohere Transcribe support (CrispASR {CohereLocalAssetManager.CrispAsrVersion}).");
    }

    /// <summary>
    /// Deactivates the plugin and stops its loopback-only sidecar process.
    /// </summary>
    public async Task DeactivateAsync()
    {
        await UnloadModelAsync();
        _assets?.SetHuggingFaceToken(null);
        _huggingFaceToken = null;
        _host = null;
    }

    /// <summary>
    /// Creates the optional Hugging Face authentication settings.
    /// </summary>
    public UserControl? CreateSettingsView() => new CohereTranscribeSettingsView(this);

    /// <summary>
    /// Selects the single Cohere Transcribe model.
    /// </summary>
    public void SelectModel(string modelId)
    {
        ValidateModelId(modelId);
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    /// <summary>
    /// Gets whether all pinned model, VAD, and language-ID files are present.
    /// </summary>
    public bool IsModelDownloaded(string modelId)
    {
        ValidateModelId(modelId);
        return _assets?.IsModelInstalled() == true;
    }

    /// <summary>
    /// Downloads and verifies the local model plus the selected Windows runtime.
    /// </summary>
    public async Task DownloadModelAsync(
        string modelId,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ValidateModelId(modelId);
        EnsureSupportedPlatform();
        var assets = GetAssets();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _accelerationStatus = new(
                TranscriptionAccelerationBackend.Cpu,
                "Downloading local model",
                "Downloading SHA-256 verified Cohere, VAD, and language-ID files.");

            var candidates = GetBackendCandidatesForCurrentMachine(_accelerationPreference);
            var initialBackend = candidates[0];
            var totalBytes = assets.ModelTransferSize + assets.GetRuntimeTransferSize(initialBackend);

            var modelProgress = progress is null
                ? null
                : new Progress<ArtifactTransferProgress>(value =>
                    progress.Report(totalBytes <= 0
                        ? 0
                        : Math.Clamp((double)value.BytesTransferred / totalBytes, 0, 1)));

            await assets.EnsureModelAsync(modelProgress, cancellationToken);

            Exception? lastError = null;
            foreach (var backend in candidates)
            {
                try
                {
                    var runtimeSize = assets.GetRuntimeTransferSize(backend);
                    var runtimeTotal = assets.ModelTransferSize + runtimeSize;
                    var runtimeProgress = progress is null
                        ? null
                        : new Progress<ArtifactTransferProgress>(value =>
                            progress.Report(Math.Clamp(
                                (double)(assets.ModelTransferSize + value.BytesTransferred) / runtimeTotal,
                                0,
                                1)));

                    _accelerationStatus = CreateInstallingStatus(backend);
                    await assets.EnsureRuntimeAsync(backend, runtimeProgress, cancellationToken);
                    progress?.Report(1);
                    _accelerationStatus = CreatePendingStatus(_accelerationPreference);
                    return;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException
                    && _accelerationPreference == TranscriptionAccelerationPreference.Auto)
                {
                    lastError = exception;
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"{GetBackendDisplayName(backend)} runtime download failed; trying the next local backend: {exception.Message}");
                }
            }

            throw lastError ?? new InvalidOperationException("No compatible CrispASR runtime is available.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _accelerationStatus = CreateUnavailableStatus(_accelerationPreference, exception.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the verified model into a persistent loopback-only CrispASR process.
    /// </summary>
    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken)
    {
        ValidateModelId(modelId);
        EnsureSupportedPlatform();

        var assets = GetAssets();
        if (!assets.IsModelInstalled())
        {
            throw new FileNotFoundException(
                "Cohere Transcribe model files are not downloaded. Download the model first.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var server = GetServer();
            var candidates = GetBackendCandidatesForCurrentMachine(_accelerationPreference);
            Exception? lastError = null;

            foreach (var backend in candidates)
            {
                try
                {
                    if (server.IsRunning
                        && server.ActiveBackend == backend
                        && string.Equals(_loadedModelId, modelId, StringComparison.Ordinal))
                    {
                        _accelerationStatus = CreateLoadedStatus(backend, fallbackDetail: null);
                        _selectedModelId = modelId;
                        return;
                    }

                    _accelerationStatus = CreateInstallingStatus(backend);
                    await assets.EnsureRuntimeAsync(backend, progress: null, cancellationToken);

                    _accelerationStatus = new(
                        TranscriptionAccelerationBackend.Cpu,
                        $"Loading with {GetBackendDisplayName(backend)}",
                        "Starting the local Cohere Transcribe runtime.");

                    var configuration = new CrispAsrServerConfiguration(
                        assets.GetRuntimeExecutablePath(backend),
                        assets.GetModelPaths(),
                        backend,
                        GetRecommendedThreadCount(Environment.ProcessorCount));

                    _loadedModelId = null;
                    await server.StartAsync(configuration, cancellationToken);

                    _loadedModelId = modelId;
                    _selectedModelId = modelId;
                    _host?.SetSetting("selectedModel", modelId);
                    _accelerationStatus = CreateLoadedStatus(
                        backend,
                        lastError is null
                            ? null
                            : $"Automatic fallback after another local backend failed: {lastError.Message}");
                    return;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException
                    && _accelerationPreference == TranscriptionAccelerationPreference.Auto)
                {
                    lastError = exception;
                    _loadedModelId = null;
                    await server.StopAsync();
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"{GetBackendDisplayName(backend)} could not load Cohere Transcribe; trying the next local backend: {exception.Message}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _loadedModelId = null;
                    await server.StopAsync();
                    _accelerationStatus = CreateUnavailableStatus(
                        _accelerationPreference,
                        exception.Message);
                    throw;
                }
            }

            _accelerationStatus = new(
                TranscriptionAccelerationBackend.Cpu,
                "Local runtime error",
                lastError?.Message ?? "No compatible CrispASR runtime is available.");
            _loadedModelId = null;
            throw new InvalidOperationException(
                "Cohere Transcribe could not start with any available local runtime.",
                lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies the host-selected acceleration preference.
    /// </summary>
    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        _accelerationPreference = preference;

        if (preference == TranscriptionAccelerationPreference.AmdRocm)
        {
            _accelerationStatus = new(
                TranscriptionAccelerationBackend.Cpu,
                "ROCm unavailable",
                "CrispASR currently provides CPU, CUDA, and Vulkan runtimes for this Windows plugin.");
            return;
        }

        if (_server?.IsRunning == true && _server.ActiveBackend is { } activeBackend)
        {
            var preferredBackend = GetPreferredBackendForStatus(preference);
            _accelerationStatus = preferredBackend == activeBackend
                ? CreateLoadedStatus(activeBackend, fallbackDetail: null)
                : new TranscriptionAccelerationStatus(
                    ToPublicBackend(activeBackend),
                    $"Using {GetBackendDisplayName(activeBackend)}",
                    $"{GetBackendDisplayName(preferredBackend)} will be used when the model reloads.");
            return;
        }

        _accelerationStatus = CreatePendingStatus(preference);
    }

    /// <summary>
    /// Transcribes WAV audio using the loaded local model.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wavAudio);
        if (translate)
            throw new NotSupportedException("Cohere Transcribe does not support speech translation.");

        var normalizedLanguage = NormalizeLanguage(language);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loadedModelId is null)
                throw new InvalidOperationException("No Cohere Transcribe model is loaded.");

            return await GetServer().TranscribeAsync(
                wavAudio,
                normalizedLanguage,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Transcribes WAV audio using the first Cohere-supported language hint.
    /// </summary>
    public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken cancellationToken)
    {
        var language = languageHints
            .Select(NormalizeLanguageOrNull)
            .FirstOrDefault(static candidate => candidate is not null);
        return TranscribeAsync(wavAudio, language, translate, prompt, cancellationToken);
    }

    /// <summary>
    /// Stops the local sidecar and releases model memory.
    /// </summary>
    public async Task UnloadModelAsync()
    {
        if (_disposed)
            return;

        await _gate.WaitAsync();
        try
        {
            if (_server is not null)
                await _server.StopAsync();

            _loadedModelId = null;
            _accelerationStatus = CreatePendingStatus(_accelerationPreference);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases the local sidecar, download client, and synchronization resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _server?.Dispose();
        if (_assets is IDisposable disposableAssets)
            disposableAssets.Dispose();

        _gate.Dispose();
        _disposed = true;
    }

    internal static IReadOnlyList<CrispAsrBackend> ResolveBackendCandidates(
        TranscriptionAccelerationPreference preference,
        bool cudaAvailable,
        bool vulkanAvailable)
    {
        return preference switch
        {
            TranscriptionAccelerationPreference.Cpu => [CrispAsrBackend.Cpu],
            TranscriptionAccelerationPreference.NvidiaCuda => [CrispAsrBackend.Cuda],
            TranscriptionAccelerationPreference.AmdVulkan => [CrispAsrBackend.Vulkan],
            TranscriptionAccelerationPreference.AmdRocm =>
                throw new NotSupportedException("ROCm is not available for the local CrispASR Windows runtime."),
            _ => BuildAutomaticBackendOrder(cudaAvailable, vulkanAvailable)
        };
    }

    internal static int GetRecommendedThreadCount(int logicalProcessorCount) =>
        Math.Clamp(Math.Max(1, logicalProcessorCount / 2), 1, 12);

    internal string? HuggingFaceToken => _huggingFaceToken;

    internal IPluginLocalization? Loc => _host?.Localization;

    internal async Task SetHuggingFaceTokenAsync(string? token)
    {
        var normalized = NormalizeHuggingFaceToken(token);
        if (normalized is not null
            && string.Equals(_huggingFaceToken, normalized, StringComparison.Ordinal))
        {
            return;
        }

        if (_host is not null)
        {
            if (normalized is null)
                await _host.DeleteSecretAsync(HuggingFaceTokenSecretName);
            else
                await _host.StoreSecretAsync(HuggingFaceTokenSecretName, normalized);
        }

        _huggingFaceToken = normalized;
        _assets?.SetHuggingFaceToken(normalized);
    }

    internal static string? NormalizeHuggingFaceToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim();
        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Hugging Face tokens cannot contain whitespace.",
                nameof(token));
        }

        return normalized;
    }

    internal static bool IsExpectedSecretStorageFailure(Exception exception) =>
        exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.Cryptography.CryptographicException
            or System.Security.SecurityException;

    internal static string? NormalizeLanguageOrNull(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)
            || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = language.Trim().ToLowerInvariant().Replace('_', '-');
        if (Languages.Contains(normalized, StringComparer.Ordinal))
            return normalized;

        var separator = normalized.IndexOf('-');
        if (separator > 0)
        {
            var primary = normalized[..separator];
            if (Languages.Contains(primary, StringComparer.Ordinal))
                return primary;
        }

        return null;
    }

    private static string? NormalizeLanguage(string? language)
    {
        var normalized = NormalizeLanguageOrNull(language);
        if (normalized is not null
            || string.IsNullOrWhiteSpace(language)
            || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        throw new NotSupportedException(
            $"Cohere Transcribe does not support language '{language}'.");
    }

    private static IReadOnlyList<CrispAsrBackend> BuildAutomaticBackendOrder(
        bool cudaAvailable,
        bool vulkanAvailable)
    {
        var backends = new List<CrispAsrBackend>(3);
        if (cudaAvailable)
            backends.Add(CrispAsrBackend.Cuda);
        if (vulkanAvailable)
            backends.Add(CrispAsrBackend.Vulkan);
        backends.Add(CrispAsrBackend.Cpu);
        return backends;
    }

    private static IReadOnlyList<CrispAsrBackend> GetBackendCandidatesForCurrentMachine(
        TranscriptionAccelerationPreference preference)
    {
        var cudaAvailable = IsNativeLibraryAvailable("nvcuda.dll");
        var vulkanAvailable = IsNativeLibraryAvailable("vulkan-1.dll");

        if (preference == TranscriptionAccelerationPreference.NvidiaCuda && !cudaAvailable)
        {
            throw new InvalidOperationException(
                "NVIDIA CUDA was selected, but the NVIDIA driver runtime (nvcuda.dll) is unavailable.");
        }

        if (preference == TranscriptionAccelerationPreference.AmdVulkan && !vulkanAvailable)
        {
            throw new InvalidOperationException(
                "Vulkan was selected, but the Vulkan loader (vulkan-1.dll) is unavailable.");
        }

        return ResolveBackendCandidates(preference, cudaAvailable, vulkanAvailable);
    }

    private static bool IsNativeLibraryAvailable(string libraryName)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (!NativeLibrary.TryLoad(libraryName, out var handle))
            return false;

        NativeLibrary.Free(handle);
        return true;
    }

    private static TranscriptionAccelerationStatus CreatePendingStatus(
        TranscriptionAccelerationPreference preference)
    {
        if (preference == TranscriptionAccelerationPreference.AmdRocm)
        {
            return new(
                TranscriptionAccelerationBackend.Cpu,
                "ROCm unavailable",
                "CrispASR currently provides CPU, CUDA, and Vulkan runtimes for this Windows plugin.");
        }

        var backend = GetPreferredBackendForStatus(preference);
        return new(
            TranscriptionAccelerationBackend.Cpu,
            "Not loaded",
            $"{GetBackendDisplayName(backend)} will be used when the model loads.");
    }

    private static CrispAsrBackend GetPreferredBackendForStatus(
        TranscriptionAccelerationPreference preference) =>
        preference switch
        {
            TranscriptionAccelerationPreference.Cpu => CrispAsrBackend.Cpu,
            TranscriptionAccelerationPreference.NvidiaCuda => CrispAsrBackend.Cuda,
            TranscriptionAccelerationPreference.AmdVulkan => CrispAsrBackend.Vulkan,
            _ => ResolveBackendCandidates(
                TranscriptionAccelerationPreference.Auto,
                IsNativeLibraryAvailable("nvcuda.dll"),
                IsNativeLibraryAvailable("vulkan-1.dll"))[0]
        };

    private static TranscriptionAccelerationStatus CreateInstallingStatus(CrispAsrBackend backend) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            $"Preparing {GetBackendDisplayName(backend)}",
            $"Installing the verified local CrispASR {GetBackendDisplayName(backend)} runtime if needed.");

    private static TranscriptionAccelerationStatus CreateLoadedStatus(
        CrispAsrBackend backend,
        string? fallbackDetail) =>
        new(
            ToPublicBackend(backend),
            $"Using {GetBackendDisplayName(backend)}",
            fallbackDetail ?? $"CrispASR {CohereLocalAssetManager.CrispAsrVersion}, local loopback process.");

    private static TranscriptionAccelerationStatus CreateUnavailableStatus(
        TranscriptionAccelerationPreference preference,
        string detail)
    {
        var displayName = preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda => "CUDA unavailable",
            TranscriptionAccelerationPreference.AmdVulkan => "Vulkan unavailable",
            TranscriptionAccelerationPreference.AmdRocm => "ROCm unavailable",
            _ => "Local runtime error"
        };

        return new(TranscriptionAccelerationBackend.Cpu, displayName, detail);
    }

    private static TranscriptionAccelerationBackend ToPublicBackend(CrispAsrBackend backend) =>
        backend switch
        {
            CrispAsrBackend.Cpu => TranscriptionAccelerationBackend.Cpu,
            CrispAsrBackend.Cuda => TranscriptionAccelerationBackend.NvidiaCuda,
            CrispAsrBackend.Vulkan => TranscriptionAccelerationBackend.AmdVulkan,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };

    private static string GetBackendDisplayName(CrispAsrBackend backend) =>
        backend switch
        {
            CrispAsrBackend.Cpu => "CPU",
            CrispAsrBackend.Cuda => "CUDA",
            CrispAsrBackend.Vulkan => "Vulkan",
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };

    private static void ValidateModelId(string modelId)
    {
        if (!string.Equals(modelId, ModelId, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown model: {modelId}", nameof(modelId));
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "Cohere Transcribe local inference currently requires Windows x64.");
        }
    }

    private ICohereLocalAssetManager GetAssets() =>
        _assets ?? throw new InvalidOperationException("Plugin is not activated.");

    private ICrispAsrServer GetServer() =>
        _server ?? throw new InvalidOperationException("Plugin is not activated.");
}
