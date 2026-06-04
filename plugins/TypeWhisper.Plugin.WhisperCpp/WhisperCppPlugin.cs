using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
/// Provides whisper cpp plugin behavior.
/// </summary>
public sealed class WhisperCppPlugin : ITypeWhisperPlugin, ITranscriptionEnginePlugin
{
    private const string CudaRuntimeDependencyHint =
        "Missing CUDA/cuBLAS runtime dependency cublas64_13.dll. TypeWhisper can download it when NVIDIA CUDA is selected.";
    private const string CudaFallbackDetail =
        "CUDA runtime could not be loaded; using CPU. " + CudaRuntimeDependencyHint;
    private const string CudaLoadFailureDetail =
        "CUDA runtime could not be loaded. " + CudaRuntimeDependencyHint;
    internal const string RocmLibraryPathEnvironmentVariable = "TYPEWHISPER_WHISPERCPP_ROCM_LIBRARY_PATH";
    private const string VulkanFallbackDetail =
        "Vulkan runtime could not be loaded; using CPU. Make sure the AMD Vulkan driver is installed.";
    private const string VulkanLoadFailureDetail =
        "Vulkan runtime could not be loaded. Make sure the AMD Vulkan driver is installed.";
    private const string RocmHookMissingDetail =
        "Set TYPEWHISPER_WHISPERCPP_ROCM_LIBRARY_PATH to a custom ROCm whisper.dll path and restart TypeWhisper.";

    private static readonly IReadOnlyList<ModelDefinition> Models =
    [
        new("tiny", "Tiny", GgmlType.Tiny, QuantizationType.NoQuantization, "ggml-tiny.bin", "~75 MB", 75, 99, false),
        new("tiny.en", "Tiny (English)", GgmlType.TinyEn, QuantizationType.NoQuantization, "ggml-tiny.en.bin", "~75 MB", 75, 1, false),
        new("tiny-q5_0", "Tiny (Q5_0)", GgmlType.Tiny, QuantizationType.Q5_0, "ggml-tiny-q5_0.bin", "~31 MB", 31, 99, false),
        new("base", "Base", GgmlType.Base, QuantizationType.NoQuantization, "ggml-base.bin", "~142 MB", 142, 99, true),
        new("base.en", "Base (English)", GgmlType.BaseEn, QuantizationType.NoQuantization, "ggml-base.en.bin", "~142 MB", 142, 1, false),
        new("base-q5_0", "Base (Q5_0)", GgmlType.Base, QuantizationType.Q5_0, "ggml-base-q5_0.bin", "~57 MB", 57, 99, true),
        new("small", "Small", GgmlType.Small, QuantizationType.NoQuantization, "ggml-small.bin", "~466 MB", 466, 99, false),
        new("small.en", "Small (English)", GgmlType.SmallEn, QuantizationType.NoQuantization, "ggml-small.en.bin", "~466 MB", 466, 1, false),
        new("small-q5_0", "Small (Q5_0)", GgmlType.Small, QuantizationType.Q5_0, "ggml-small-q5_0.bin", "~182 MB", 182, 99, false),
        new("medium", "Medium", GgmlType.Medium, QuantizationType.NoQuantization, "ggml-medium.bin", "~1.5 GB", 1530, 99, false),
        new("medium.en", "Medium (English)", GgmlType.MediumEn, QuantizationType.NoQuantization, "ggml-medium.en.bin", "~1.5 GB", 1530, 1, false),
        new("medium-q5_0", "Medium (Q5_0)", GgmlType.Medium, QuantizationType.Q5_0, "ggml-medium-q5_0.bin", "~601 MB", 601, 99, false),
        new("large-v3-turbo", "Large V3 Turbo", GgmlType.LargeV3Turbo, QuantizationType.NoQuantization, "ggml-large-v3-turbo.bin", "~1.6 GB", 1620, 99, false),
        new("large-v3-turbo-q5_0", "Large V3 Turbo (Q5_0)", GgmlType.LargeV3Turbo, QuantizationType.Q5_0, "ggml-large-v3-turbo-q5_0.bin", "~684 MB", 684, 99, false),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient = new();
    private IWhisperCppCudaRuntimeInstaller? _cudaRuntimeInstaller;
    private IPluginHostServices? _host;
    private WhisperFactory? _factory;
    private string? _selectedModelId;
    private string? _loadedModelId;
    private string? _pluginDirectory;
    private bool _cudaRuntimeRestartRequired;
    private TranscriptionAccelerationPreference _accelerationPreference = TranscriptionAccelerationPreference.Auto;
    private bool _customRocmRuntimeLoaded;
    private bool _runtimeRestartRequired;
    private TranscriptionAccelerationStatus _accelerationStatus = new(
        TranscriptionAccelerationBackend.Cpu,
        "Using CPU");

    /// <summary>
    /// Initializes a new instance of the WhisperCppPlugin class.
    /// </summary>
    public WhisperCppPlugin()
    {
    }

    internal WhisperCppPlugin(IWhisperCppCudaRuntimeInstaller cudaRuntimeInstaller)
    {
        _cudaRuntimeInstaller = cudaRuntimeInstaller;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.whisper-cpp";
    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public string PluginName => "whisper.cpp (Local)";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.2";

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "whisper-cpp";
    /// <summary>
    /// Gets the provider display name.
    /// </summary>
    public string ProviderDisplayName => "Local (whisper.cpp)";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => true;
    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;
    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => true;
    /// <summary>
    /// Gets whether the provider can download models through the host.
    /// </summary>
    public bool SupportsModelDownload => true;
    /// <summary>
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => [];
    /// <summary>
    /// Gets the supported acceleration backends.
    /// </summary>
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
    [
        TranscriptionAccelerationBackend.Cpu,
        TranscriptionAccelerationBackend.NvidiaCuda,
        TranscriptionAccelerationBackend.AmdVulkan,
        TranscriptionAccelerationBackend.AmdRocm
    ];
    /// <summary>
    /// Gets the acceleration preference.
    /// </summary>
    public TranscriptionAccelerationPreference AccelerationPreference => _accelerationPreference;
    /// <summary>
    /// Gets the acceleration status.
    /// </summary>
    public TranscriptionAccelerationStatus AccelerationStatus => _accelerationStatus;

    /// <summary>
    /// Gets the transcription models.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = Models.Select(model =>
        new PluginModelInfo(model.Id, model.DisplayName)
        {
            SizeDescription = model.SizeDescription,
            EstimatedSizeMB = model.EstimatedSizeMB,
            IsRecommended = model.IsRecommended,
            LanguageCount = model.LanguageCount,
        }).ToList();

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _pluginDirectory = Path.GetDirectoryName(typeof(WhisperCppPlugin).Assembly.Location);
        if (_pluginDirectory is not null)
            _cudaRuntimeInstaller ??= new WhisperCppCudaRuntimeInstaller(_pluginDirectory, _httpClient);
        _selectedModelId = host.GetSetting<string>("selectedModel");
        host.Log(PluginLogLevel.Info, "Activated");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public async Task DeactivateAsync()
    {
        await UnloadModelAsync();
        _host = null;
        _pluginDirectory = null;
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => null;

    /// <summary>
    /// Sets acceleration preference.
    /// </summary>
    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        _runtimeRestartRequired = RequiresRuntimeRestart(preference)
            || preference == TranscriptionAccelerationPreference.NvidiaCuda && _cudaRuntimeRestartRequired;
        _accelerationPreference = preference;
        if (!_runtimeRestartRequired)
            ApplyRuntimeConfiguration(preference);

        if (preference == TranscriptionAccelerationPreference.NvidiaCuda && _cudaRuntimeRestartRequired)
        {
            _accelerationStatus = CreateCudaRuntimeInstalledRestartRequiredStatus();
            return;
        }

        _accelerationStatus = _runtimeRestartRequired
            ? CreateRuntimeRestartStatus(preference)
            : CreatePendingAccelerationStatus(
                preference,
                _cudaRuntimeInstaller?.IsInstalled == true,
                ResolveRocmLibraryPathFromEnvironment() is not null);
    }

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        _ = GetModel(modelId);
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    /// <summary>
    /// Gets whether the requested model is available locally.
    /// </summary>
    public bool IsModelDownloaded(string modelId) => File.Exists(GetModelPath(modelId));

    /// <summary>
    /// Downloads the requested model and reports progress when available.
    /// </summary>
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var model = GetModel(modelId);
            var modelPath = GetModelPath(modelId);
            var modelDirectory = Path.GetDirectoryName(modelPath)!;
            Directory.CreateDirectory(modelDirectory);

            if (File.Exists(modelPath))
            {
                progress?.Report(1.0);
                return;
            }

            var tempPath = Path.Combine(modelDirectory, $"{Path.GetFileName(modelPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using var modelStream = await WhisperGgmlDownloader.Default
                    .GetGgmlModelAsync(model.Type, model.Quantization, ct);

                var buffer = new byte[81920];
                long bytesCopied = 0;
                var totalBytes = modelStream.CanSeek ? modelStream.Length : 0;

                await using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    while (true)
                    {
                        var read = await modelStream.ReadAsync(buffer, ct);
                        if (read == 0)
                            break;

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        bytesCopied += read;

                        if (totalBytes > 0)
                            progress?.Report((double)bytesCopied / totalBytes);
                    }

                    await fileStream.FlushAsync(ct);
                }

                if (File.Exists(modelPath))
                    File.Delete(modelPath);

                File.Move(tempPath, modelPath);
                progress?.Report(1.0);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the selected transcription model into memory.
    /// </summary>
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var modelPath = GetModelPath(modelId);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model files not found for: {modelId}", modelPath);

        await _gate.WaitAsync(ct);
        try
        {
            if (_accelerationStatus.RequiresRestart)
                throw new InvalidOperationException(_accelerationStatus.Detail);

            ApplyRuntimeConfiguration(_accelerationPreference);
            await EnsureCudaRuntimeAvailableForLoadAsync(ct);
            EnsureRocmRuntimeAvailableForLoad();
            DisposeFactoryUnsafe();
            try
            {
                _factory = WhisperFactory.FromPath(modelPath);
            }
            catch (Exception ex) when (IsNativeLoadFailure(ex))
            {
                var loadException = CreateNativeLoadFailureException(ex);
                _accelerationStatus = CreateNativeLoadFailureStatus(
                    loadException,
                    _accelerationPreference);
                throw _accelerationPreference == TranscriptionAccelerationPreference.NvidiaCuda
                    ? new InvalidOperationException(_accelerationStatus.Detail, loadException)
                    : loadException;
            }

            var loadedLibrary = RuntimeOptions.LoadedLibrary;
            _accelerationStatus = CreateLoadedAccelerationStatus(
                loadedLibrary,
                _accelerationPreference);
            _customRocmRuntimeLoaded = _accelerationPreference == TranscriptionAccelerationPreference.AmdRocm;
            _loadedModelId = modelId;
            _selectedModelId = modelId;
            _host?.SetSetting("selectedModel", modelId);
            _host?.Log(
                PluginLogLevel.Info,
                $"Loaded model {modelId} using runtime {loadedLibrary?.ToString() ?? "unknown"} ({_accelerationStatus.DisplayText})");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory is null || _loadedModelId is null)
                throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

            var builder = _factory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language);

            if (!string.IsNullOrWhiteSpace(prompt))
                builder.WithPrompt(prompt);

            if (translate)
                builder.WithTranslate();

            using var processor = builder.Build();
            await using var audioStream = new MemoryStream(wavAudio, writable: false);

            var text = new StringBuilder();
            string? detectedLanguage = null;
            double durationSeconds = 0;
            float? noSpeechProbability = null;

            await foreach (var segment in processor.ProcessAsync(audioStream, ct))
            {
                var segmentText = segment.Text.Trim();
                if (segmentText.Length > 0)
                {
                    if (text.Length > 0)
                        text.Append(' ');

                    text.Append(segmentText);
                }

                if (string.IsNullOrWhiteSpace(detectedLanguage) && !string.IsNullOrWhiteSpace(segment.Language))
                    detectedLanguage = segment.Language;

                durationSeconds = Math.Max(durationSeconds, segment.End.TotalSeconds);
                noSpeechProbability = segment.NoSpeechProbability;
            }

            return new PluginTranscriptionResult(
                text.ToString().Trim(),
                detectedLanguage,
                durationSeconds,
                noSpeechProbability);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Unloads model asynchronously..
    /// </summary>
    public async Task UnloadModelAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DisposeFactoryUnsafe();
            _loadedModelId = null;
            _selectedModelId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        DisposeFactoryUnsafe();
        if (_cudaRuntimeInstaller is IDisposable disposableInstaller)
            disposableInstaller.Dispose();
        _httpClient.Dispose();
        _gate.Dispose();
    }

    private ModelDefinition GetModel(string modelId) => Models.FirstOrDefault(model => model.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    internal static IReadOnlyList<RuntimeLibrary> GetRuntimeLibraryOrder(
        TranscriptionAccelerationPreference preference) =>
        preference switch
        {
            TranscriptionAccelerationPreference.Cpu => [RuntimeLibrary.Cpu],
            TranscriptionAccelerationPreference.NvidiaCuda => [RuntimeLibrary.Cuda],
            TranscriptionAccelerationPreference.AmdVulkan => [RuntimeLibrary.Vulkan],
            TranscriptionAccelerationPreference.AmdRocm => [],
            _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
        };

    private static void ApplyRuntimeConfiguration(TranscriptionAccelerationPreference preference)
    {
        RuntimeOptions.LibraryPath = preference == TranscriptionAccelerationPreference.AmdRocm
            ? ResolveRocmLibraryPathFromEnvironment()
            : null;
        RuntimeOptions.RuntimeLibraryOrder = GetRuntimeLibraryOrder(preference).ToList();
    }

    private static TranscriptionAccelerationStatus CreatePendingAccelerationStatus(
        TranscriptionAccelerationPreference preference,
        bool cudaRuntimeInstalled,
        bool rocmLibraryConfigured) =>
        preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda when cudaRuntimeInstalled => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "CUDA will be used when the model loads."),
            TranscriptionAccelerationPreference.NvidiaCuda => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "CUDA support will be downloaded when the model loads."),
            TranscriptionAccelerationPreference.AmdVulkan => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "Vulkan will be used when the model loads."),
            TranscriptionAccelerationPreference.AmdRocm when rocmLibraryConfigured => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "ROCm will be used when the model loads."),
            TranscriptionAccelerationPreference.AmdRocm => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "ROCm unavailable",
                    RocmHookMissingDetail),
            _ => new(TranscriptionAccelerationBackend.Cpu, "Using CPU")
        };

    private static TranscriptionAccelerationStatus CreateLoadedAccelerationStatus(
        RuntimeLibrary? loadedLibrary,
        TranscriptionAccelerationPreference preference)
    {
        if (preference == TranscriptionAccelerationPreference.AmdRocm)
            return new(TranscriptionAccelerationBackend.AmdRocm, "Using ROCm");

        return loadedLibrary switch
        {
            RuntimeLibrary.Cuda => new(TranscriptionAccelerationBackend.NvidiaCuda, "Using CUDA"),
            RuntimeLibrary.Vulkan => new(TranscriptionAccelerationBackend.AmdVulkan, "Using Vulkan"),
            RuntimeLibrary.Cpu => preference switch
            {
                TranscriptionAccelerationPreference.Auto => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "CUDA/Vulkan runtime was not selected or could not be loaded."),
                TranscriptionAccelerationPreference.NvidiaCuda => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "CUDA unavailable",
                    CudaFallbackDetail),
                TranscriptionAccelerationPreference.AmdVulkan => new(
                    TranscriptionAccelerationBackend.Cpu,
                    "Vulkan unavailable",
                    VulkanFallbackDetail),
                _ => new(TranscriptionAccelerationBackend.Cpu, "Using CPU")
            },
            _ => new(TranscriptionAccelerationBackend.Cpu, "Using CPU")
        };
    }

    private static TranscriptionAccelerationStatus CreateNativeLoadFailureStatus(
        Exception error,
        TranscriptionAccelerationPreference preference) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            GetUnavailableDisplayText(preference),
            GetNativeLoadFailureDetail(error, preference));

    private async Task EnsureCudaRuntimeAvailableForLoadAsync(CancellationToken cancellationToken)
    {
        if (_accelerationPreference != TranscriptionAccelerationPreference.NvidiaCuda)
            return;

        if (_cudaRuntimeRestartRequired)
        {
            _accelerationStatus = CreateCudaRuntimeInstalledRestartRequiredStatus();
            throw new InvalidOperationException(_accelerationStatus.Detail);
        }

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            _accelerationStatus = CreateCudaRuntimeInstallFailureStatus(
                "NVIDIA CUDA acceleration for whisper.cpp is only available on Windows x64.");
            throw new InvalidOperationException(_accelerationStatus.Detail);
        }

        var installer = _cudaRuntimeInstaller
            ?? throw new InvalidOperationException("The whisper.cpp CUDA runtime installer is not available.");

        if (installer.IsInstalled)
            return;

        _accelerationStatus = new(
            TranscriptionAccelerationBackend.Cpu,
            "Installing CUDA support",
            "Downloading the NVIDIA CUDA runtime needed for whisper.cpp.");
        _host?.Log(PluginLogLevel.Info, "Installing NVIDIA CUDA runtime for whisper.cpp.");

        try
        {
            await installer.EnsureInstalledAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _accelerationStatus = CreateCudaRuntimeInstallFailureStatus(
                "CUDA support download failed. " + ex.Message);
            throw new InvalidOperationException(_accelerationStatus.Detail, ex);
        }

        if (!installer.IsInstalled)
        {
            _accelerationStatus = CreateCudaRuntimeInstallFailureStatus(
                "CUDA support download completed, but the required NVIDIA runtime files are still missing.");
            throw new InvalidOperationException(_accelerationStatus.Detail);
        }

        _host?.Log(
            PluginLogLevel.Info,
            $"Installed NVIDIA CUDA runtime for whisper.cpp at {installer.RuntimeDirectory}.");

        _cudaRuntimeRestartRequired = true;
        _accelerationStatus = CreateCudaRuntimeInstalledRestartRequiredStatus();
        throw new InvalidOperationException(_accelerationStatus.Detail);
    }

    private static TranscriptionAccelerationStatus CreateCudaRuntimeInstallFailureStatus(string detail) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            "CUDA unavailable",
            detail);

    private void EnsureRocmRuntimeAvailableForLoad()
    {
        if (_accelerationPreference != TranscriptionAccelerationPreference.AmdRocm)
            return;

        if (_runtimeRestartRequired)
        {
            _accelerationStatus = CreateRuntimeRestartStatus(_accelerationPreference);
            throw new InvalidOperationException(_accelerationStatus.Detail);
        }

        var libraryPath = ResolveRocmLibraryPathFromEnvironment();
        if (libraryPath is not null)
        {
            RuntimeOptions.LibraryPath = libraryPath;
            return;
        }

        _accelerationStatus = new(
            TranscriptionAccelerationBackend.Cpu,
            "ROCm unavailable",
            RocmHookMissingDetail);
        throw new InvalidOperationException(_accelerationStatus.Detail);
    }

    private static TranscriptionAccelerationStatus CreateCudaRuntimeInstalledRestartRequiredStatus() =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            "Restart required",
            "CUDA support was installed. Restart TypeWhisper to load the CUDA runtime.",
            RequiresRestart: true);

    private string GetModelPath(string modelId)
    {
        var host = _host ?? throw new InvalidOperationException("Plugin is not activated.");
        var model = GetModel(modelId);
        return Path.Combine(host.PluginDataDirectory, "Models", model.FileName);
    }

    internal static string BuildNativeLoadFailureMessage(
        string pluginDirectory,
        string runtimeIdentifier,
        Exception error)
    {
        var safeRuntimeIdentifier = Path.GetFileName(
            runtimeIdentifier.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(safeRuntimeIdentifier))
            safeRuntimeIdentifier = runtimeIdentifier;

        var runtimeDirectory = Path.Join(pluginDirectory, "runtimes", safeRuntimeIdentifier);
        var cudaRuntimeDirectory = Path.Join(pluginDirectory, "runtimes", "cuda", safeRuntimeIdentifier);
        var vulkanRuntimeDirectory = Path.Join(pluginDirectory, "runtimes", "vulkan", safeRuntimeIdentifier);
        const string requiredFiles =
            "whisper.dll, ggml-whisper.dll, ggml-base-whisper.dll, ggml-cpu-whisper.dll, " +
            "ggml-cuda-whisper.dll (for CUDA), cublas64_13.dll (for CUDA/cuBLAS), " +
            "ggml-vulkan-whisper.dll (for Vulkan), " +
            "msvcp140.dll, vcruntime140.dll, " +
            "vcruntime140_1.dll, VCOMP140.DLL";

        return "Unable to load the whisper.cpp native runtime. " +
            $"Expected CPU native DLLs under '{runtimeDirectory}' and CUDA native DLLs under " +
            $"'{cudaRuntimeDirectory}', Vulkan native DLLs under '{vulkanRuntimeDirectory}', " +
            $"including {requiredFiles}. " +
            CudaRuntimeDependencyHint + " " +
            $"For ROCm, set {RocmLibraryPathEnvironmentVariable} to a custom ROCm whisper.dll. " +
            "Reinstall or update the whisper.cpp plugin. If the problem persists, install the " +
            "Microsoft Visual C++ 2015-2022 Redistributable for your Windows architecture. " +
            $"Original error: {error.Message}";
    }

    internal static bool IsNativeLoadFailure(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or BadImageFormatException)
                return true;

            if (current.Message.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("0x8007007E", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private InvalidOperationException CreateNativeLoadFailureException(Exception error)
    {
        var pluginDirectory = _pluginDirectory
            ?? Path.GetDirectoryName(typeof(WhisperCppPlugin).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var message = BuildNativeLoadFailureMessage(
            pluginDirectory,
            RuntimeInformation.RuntimeIdentifier,
            error);
        return new InvalidOperationException(message, error);
    }

    private void DisposeFactoryUnsafe()
    {
        _factory?.Dispose();
        _factory = null;
    }

    internal static string? ResolveRocmLibraryPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(configuredPath.Trim());
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            return null;
        }

        if (Directory.Exists(candidate))
            candidate = Path.Join(candidate, "whisper.dll");

        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveRocmLibraryPathFromEnvironment() =>
        ResolveRocmLibraryPath(Environment.GetEnvironmentVariable(RocmLibraryPathEnvironmentVariable));

    private bool RequiresRuntimeRestart(TranscriptionAccelerationPreference targetPreference)
    {
        if (_customRocmRuntimeLoaded)
            return targetPreference != TranscriptionAccelerationPreference.AmdRocm;

        if (targetPreference == TranscriptionAccelerationPreference.AmdRocm)
            return RuntimeOptions.LoadedLibrary is not null;

        return RuntimeOptions.LoadedLibrary is { } loadedLibrary
            && !IsLoadedLibraryCompatibleWithPreference(loadedLibrary, targetPreference);
    }

    private static bool IsLoadedLibraryCompatibleWithPreference(
        RuntimeLibrary loadedLibrary,
        TranscriptionAccelerationPreference preference) =>
        preference switch
        {
            TranscriptionAccelerationPreference.Auto => true,
            TranscriptionAccelerationPreference.Cpu => loadedLibrary == RuntimeLibrary.Cpu,
            TranscriptionAccelerationPreference.NvidiaCuda => loadedLibrary == RuntimeLibrary.Cuda,
            TranscriptionAccelerationPreference.AmdVulkan => loadedLibrary == RuntimeLibrary.Vulkan,
            TranscriptionAccelerationPreference.AmdRocm => false,
            _ => false
        };

    private TranscriptionAccelerationStatus CreateRuntimeRestartStatus(
        TranscriptionAccelerationPreference targetPreference) =>
        new(
            GetCurrentRuntimeBackend(),
            GetCurrentRuntimeDisplayText(),
            $"Restart TypeWhisper to switch whisper.cpp to {GetPreferenceDisplayName(targetPreference)}.",
            RequiresRestart: true);

    private TranscriptionAccelerationBackend GetCurrentRuntimeBackend()
    {
        if (_customRocmRuntimeLoaded)
            return TranscriptionAccelerationBackend.AmdRocm;

        return RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.Cuda => TranscriptionAccelerationBackend.NvidiaCuda,
            RuntimeLibrary.Vulkan => TranscriptionAccelerationBackend.AmdVulkan,
            _ => TranscriptionAccelerationBackend.Cpu
        };
    }

    private string GetCurrentRuntimeDisplayText()
    {
        if (_customRocmRuntimeLoaded)
            return "Using ROCm";

        return RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.Cuda => "Using CUDA",
            RuntimeLibrary.Vulkan => "Using Vulkan",
            _ => "Using CPU"
        };
    }

    private static string GetPreferenceDisplayName(TranscriptionAccelerationPreference preference) =>
        preference switch
        {
            TranscriptionAccelerationPreference.Cpu => "CPU",
            TranscriptionAccelerationPreference.NvidiaCuda => "CUDA",
            TranscriptionAccelerationPreference.AmdVulkan => "Vulkan",
            TranscriptionAccelerationPreference.AmdRocm => "ROCm",
            _ => "automatic acceleration"
        };

    private static string GetUnavailableDisplayText(TranscriptionAccelerationPreference preference) =>
        preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda => "CUDA unavailable",
            TranscriptionAccelerationPreference.AmdVulkan => "Vulkan unavailable",
            TranscriptionAccelerationPreference.AmdRocm => "ROCm unavailable",
            _ => "Native runtime unavailable"
        };

    private static string GetNativeLoadFailureDetail(
        Exception error,
        TranscriptionAccelerationPreference preference) =>
        preference switch
        {
            TranscriptionAccelerationPreference.NvidiaCuda => CudaLoadFailureDetail,
            TranscriptionAccelerationPreference.AmdVulkan => VulkanLoadFailureDetail,
            TranscriptionAccelerationPreference.AmdRocm =>
                $"ROCm runtime could not be loaded from {RocmLibraryPathEnvironmentVariable}. {error.Message}",
            _ => error.Message
        };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record ModelDefinition(
        string Id,
        string DisplayName,
        GgmlType Type,
        QuantizationType Quantization,
        string FileName,
        string SizeDescription,
        long EstimatedSizeMB,
        int LanguageCount,
        bool IsRecommended);
}
