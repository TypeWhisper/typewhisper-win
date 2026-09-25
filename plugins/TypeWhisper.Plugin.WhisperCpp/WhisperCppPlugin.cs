using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
/// Provides whisper cpp plugin behavior.
/// </summary>
public sealed partial class WhisperCppPlugin :
    ITypeWhisperPlugin,
    IPcmTranscriptionEnginePlugin,
    IPluginTextSettings,
    ITranscriptionAccelerationDiagnosticsProvider
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

    // Exact artifact sizes from whisper.net v3, revision a28c1f379c0359332f8d101184249cba28b1ff53.
    // https://huggingface.co/sandrohanea/whisper.net/tree/a28c1f379c0359332f8d101184249cba28b1ff53
    private static readonly IReadOnlyList<ModelDefinition> Models =
    [
        new("tiny", "Tiny", GgmlType.Tiny, QuantizationType.NoQuantization, "ggml-tiny.bin", "~75 MB", 75, 99, false, 77691713),
        new("tiny.en", "Tiny (English)", GgmlType.TinyEn, QuantizationType.NoQuantization, "ggml-tiny.en.bin", "~75 MB", 75, 1, false, 77704715),
        new("tiny-q5_0", "Tiny (Q5_0)", GgmlType.Tiny, QuantizationType.Q5_0, "ggml-tiny-q5_0.bin", "~31 MB", 31, 99, false, 29875721),
        new("base", "Base", GgmlType.Base, QuantizationType.NoQuantization, "ggml-base.bin", "~142 MB", 142, 99, true, 147951465),
        new("base.en", "Base (English)", GgmlType.BaseEn, QuantizationType.NoQuantization, "ggml-base.en.bin", "~142 MB", 142, 1, false, 147964211),
        new("base-q5_0", "Base (Q5_0)", GgmlType.Base, QuantizationType.Q5_0, "ggml-base-q5_0.bin", "~57 MB", 57, 99, true, 55295433),
        new("small", "Small", GgmlType.Small, QuantizationType.NoQuantization, "ggml-small.bin", "~466 MB", 466, 99, false, 487601967),
        new("small.en", "Small (English)", GgmlType.SmallEn, QuantizationType.NoQuantization, "ggml-small.en.bin", "~466 MB", 466, 1, false, 487614201),
        new("small-q5_0", "Small (Q5_0)", GgmlType.Small, QuantizationType.Q5_0, "ggml-small-q5_0.bin", "~182 MB", 182, 99, false, 175209663),
        new("medium", "Medium", GgmlType.Medium, QuantizationType.NoQuantization, "ggml-medium.bin", "~1.5 GB", 1530, 99, false, 1533763059),
        new("medium.en", "Medium (English)", GgmlType.MediumEn, QuantizationType.NoQuantization, "ggml-medium.en.bin", "~1.5 GB", 1530, 1, false, 1533774781),
        new("medium-q5_0", "Medium (Q5_0)", GgmlType.Medium, QuantizationType.Q5_0, "ggml-medium-q5_0.bin", "~601 MB", 601, 99, false, 539212467),
        new("large-v3-turbo", "Large V3 Turbo", GgmlType.LargeV3Turbo, QuantizationType.NoQuantization, "ggml-large-v3-turbo.bin", "~1.6 GB", 1620, 99, false, 1624555275),
        new("large-v3-turbo-q5_0", "Large V3 Turbo (Q5_0)", GgmlType.LargeV3Turbo, QuantizationType.Q5_0, "ggml-large-v3-turbo-q5_0.bin", "~684 MB", 684, 99, false, 574041195),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private IWhisperCppCudaRuntimeInstaller? _cudaRuntimeInstaller;
    private IPluginHostServices? _host;
    private WhisperFactory? _factory;
    internal Func<string, WhisperFactory> CreateFactory { get; set; }
    internal Func<string, IReadOnlyList<GpuDevice>> ListGpuDevices { get; set; } = GpuDevices.List;
    private GpuDevice? _gpuDevice;
    internal Action<WhisperFactory> ReleaseFactory { get; set; } = factory => factory.Dispose();
    private string? _selectedModelId;
    private string? _loadedModelId;
    private string? _pluginDirectory;
    private bool _cudaRuntimeRestartRequired;
    private bool IsCudaRuntimeRestartRequired => _cudaRuntimeRestartRequired
        || (_cudaRuntimeInstaller is { } installer && AppDomain.CurrentDomain.GetData(CudaRestartGateKey(installer.RuntimeDirectory)) is true);
    private static string CudaRestartGateKey(string directory) =>
        "TypeWhisper.WhisperCpp.CudaRestart:" + Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    private TranscriptionAccelerationPreference _accelerationPreference = TranscriptionAccelerationPreference.Auto;
    private bool _customRocmRuntimeLoaded;
    private bool _runtimeRestartRequired;
    private string? _runtimePath;
    private string? _lastNativeError;
    private TranscriptionAccelerationStatus _accelerationStatus = new(
        TranscriptionAccelerationBackend.Cpu,
        "Using CPU");
    internal Func<GgmlType, QuantizationType, CancellationToken, Task<Stream>> OpenModelDownloadAsync { get; set; } =
        (type, quantization, ct) => WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, quantization, ct);

    /// <summary>
    /// Initializes a new instance of the WhisperCppPlugin class.
    /// </summary>
    public WhisperCppPlugin()
    {
        CreateFactory = path => WhisperFactory.FromPath(path, CreateFactoryOptions());
    }

    internal WhisperCppPlugin(IWhisperCppCudaRuntimeInstaller cudaRuntimeInstaller) : this()
    {
        _cudaRuntimeInstaller = cudaRuntimeInstaller;
    }

    // Loads the native runtime before the model so a Vulkan runtime can place it on the dedicated GPU.
    internal WhisperFactoryOptions CreateFactoryOptions()
    {
        _gpuDevice = null;
        var options = new WhisperFactoryOptions();
        if (_accelerationPreference is TranscriptionAccelerationPreference.Cpu or TranscriptionAccelerationPreference.AmdRocm) return options;
        try
        {
            WhisperFactory.GetRuntimeInfo();
            if (RuntimeOptions.LoadedLibrary != RuntimeLibrary.Vulkan) return options;
            var devices = ResolveRuntimePathForDiagnostics(RuntimeLibrary.Vulkan, _accelerationPreference, useRequestedBackend: false) is { } runtime
                ? ListGpuDevices(Path.GetDirectoryName(runtime)!)
                : [];
            if (devices.Count == 0)
            {
                _host?.Log(PluginLogLevel.Warning, "GPU list unavailable: no GPU could be read from the Vulkan runtime, so whisper.cpp keeps its default device.");
                return options;
            }
            options.GpuDevice = GpuDevices.Select(devices);
            _gpuDevice = devices[options.GpuDevice];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without the list whisper.cpp keeps its default device.
            _host?.Log(PluginLogLevel.Warning, "GPU list unavailable: " + GetRootCauseMessage(ex));
        }
        return options;
    }

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.whisper-cpp";
    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public string PluginName => L("Whisper (Local)", "Whisper (Lokal)");
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.2.20";

    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "whisper-cpp";
    /// <summary>
    /// Gets the provider display name.
    /// </summary>
    public string ProviderDisplayName => PluginName;
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    /// <inheritdoc />
    public bool IsConfigured => _host is not null
        && _selectedModelId is { } modelId
        && Models.Any(model => model.Id == modelId)
        && IsModelDownloaded(modelId);
    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;
    /// <summary>
    /// Gets whether the provider supports translation requests.
    /// </summary>
    public bool SupportsTranslation => _selectedModelId?.EndsWith(".en", StringComparison.Ordinal) != true;
    /// <summary>
    /// Gets whether the provider can download models through the host.
    /// </summary>
    public bool SupportsModelDownload => true;
    /// <summary>
    /// Gets whether downloaded model files can be removed.
    /// </summary>
    public bool SupportsModelRemoval => true;
    /// <summary>
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => _selectedModelId?.EndsWith(".en", StringComparison.Ordinal) == true ? ["en"] : WhisperLanguages;
    /// <inheritdoc />
    public bool SupportsLocalLivePreview => true;
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
    /// Gets support-oriented details about the selected and active native runtime.
    /// </summary>
    public TranscriptionAccelerationDiagnostics AccelerationDiagnostics => new(
        ProviderId,
        ProviderDisplayName,
        _accelerationPreference,
        _accelerationStatus.ActiveBackend,
        _runtimePath,
        _lastNativeError);

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
        _cudaRuntimeInstaller ??= new WhisperCppCudaRuntimeInstaller(host.PluginAssetDirectory, _httpClient);
        _selectedModelId = host.GetSetting<string>("selectedModel");
        if (Enum.TryParse<TranscriptionAccelerationPreference>(host.GetSetting<string>("acceleration"), out var preference) && Enum.IsDefined(preference)) SetAccelerationPreference(preference);
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
    /// Sets acceleration preference.
    /// </summary>
    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        _runtimeRestartRequired = RequiresRuntimeRestart(preference)
            || preference is (TranscriptionAccelerationPreference.Auto or TranscriptionAccelerationPreference.NvidiaCuda) && IsCudaRuntimeRestartRequired;
        _accelerationPreference = preference;
        if (!_runtimeRestartRequired)
            ApplyRuntimeConfiguration(preference);

        if (preference is (TranscriptionAccelerationPreference.Auto or TranscriptionAccelerationPreference.NvidiaCuda) && IsCudaRuntimeRestartRequired)
        {
            _accelerationStatus = CreateCudaRuntimeInstalledRestartRequiredStatus();
            return;
        }

        if (_factory is not null && !_runtimeRestartRequired)
        {
            _accelerationStatus = CreateLoadedAccelerationStatus(RuntimeOptions.LoadedLibrary, preference, _gpuDevice);
            return;
        }

        _accelerationStatus = _runtimeRestartRequired
            ? CreateRuntimeRestartStatus(preference)
            : CreatePendingAccelerationStatus(
                preference,
                _cudaRuntimeInstaller?.HasRuntimeFiles == true,
                ResolveRocmLibraryPathFromEnvironment() is not null);
    }

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        _ = GetModel(modelId);
        _host?.SetSetting("selectedModel", modelId);
        _selectedModelId = modelId;
    }

    /// <summary>
    /// Gets whether the requested model is available locally.
    /// </summary>
    public bool IsModelDownloaded(string modelId)
    {
        var model = GetModel(modelId);
        try
        {
            var file = new FileInfo(GetModelPath(modelId));
            return file.Exists && file.Length == model.ExpectedSizeBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Downloads the requested model and reports progress when available.
    /// </summary>
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var model = GetModel(modelId);
            var modelPath = GetModelPath(modelId);
            var modelDirectory = Path.GetDirectoryName(modelPath)!;
            Directory.CreateDirectory(modelDirectory);
            RemoveOrphanedModelDownloads(modelPath);

            if (IsModelDownloaded(modelId))
            {
                progress?.Report(1.0);
                return;
            }

            var tempPath = Path.Combine(modelDirectory, $"{Path.GetFileName(modelPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using var modelStream = await OpenModelDownloadAsync(model.Type, model.Quantization, ct);

                var buffer = new byte[81920];
                long bytesCopied = 0;
                var totalBytes = model.ExpectedSizeBytes;

                await using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    while (true)
                    {
                        var read = await modelStream.ReadAsync(buffer, ct);
                        if (read == 0)
                            break;

                        if (bytesCopied > totalBytes - read)
                            throw new InvalidDataException("The model download exceeds the expected artifact size.");
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        bytesCopied += read;

                        if (totalBytes > 0)
                            progress?.Report(Math.Min(0.99, (double)bytesCopied / totalBytes));
                    }

                    await fileStream.FlushAsync(ct);
                }

                ct.ThrowIfCancellationRequested();
                ValidateModelDownload(bytesCopied, model.ExpectedSizeBytes);
                File.Move(tempPath, modelPath, overwrite: true);
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

    internal static void ValidateModelDownload(long bytesCopied, long expectedSizeBytes)
    {
        if (bytesCopied != expectedSizeBytes)
            throw new InvalidDataException("The model download does not match the expected artifact size. Please retry the download.");
    }

    /// <summary>
    /// Removes the downloaded GGML file for the requested model.
    /// </summary>
    public async Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        var modelPath = GetModelPath(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(_selectedModelId, modelId, StringComparison.Ordinal))
            {
                _host?.SetSetting<string?>("selectedModel", null);
                _selectedModelId = null;
            }
            if (string.Equals(_loadedModelId, modelId, StringComparison.Ordinal))
            {
                DisposeFactoryUnsafe();
                _loadedModelId = null;
            }

            if (File.Exists(modelPath))
                File.Delete(modelPath);
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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await LoadModelCoreAsync(modelId, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // Caller holds _gate, so preview and final decoding share one loaded model.
    private async Task LoadModelCoreAsync(string modelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_factory is not null && _loadedModelId == modelId && !_accelerationStatus.RequiresRestart) return;
        var modelPath = GetModelPath(modelId);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model files not found for: {modelId}", modelPath);
        if (!IsModelDownloaded(modelId))
            throw new InvalidDataException("The model file is incomplete. Please download the model again.");
        if (_accelerationStatus.RequiresRestart)
            throw new InvalidOperationException(_accelerationStatus.Detail);

        ApplyRuntimeConfiguration(_accelerationPreference);
        var cudaVerified = await EnsureCudaRuntimeAvailableForLoadAsync(ct).ConfigureAwait(false);
        EnsureRocmRuntimeAvailableForLoad();
        await Task.Run(() => PrepareCudaRuntimeSearchPath(cudaVerified), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Keep only one native context: overlapping large models can exhaust RAM/VRAM.
        // The saved selection survives failure/cancellation and is reloaded on the next decode.
        DisposeFactoryUnsafe();
        try
        {
            var factory = await Task.Run(() => CreateFactory(modelPath), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                ReleaseFactory(factory);
                ct.ThrowIfCancellationRequested();
            }
            _factory = factory;
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            _runtimePath = ResolveRuntimePathForDiagnostics(
                RuntimeOptions.LoadedLibrary,
                _accelerationPreference,
                useRequestedBackend: true);
            _lastNativeError = GetRootCauseMessage(ex);
            var loadException = CreateNativeLoadFailureException(ex);
            _accelerationStatus = CreateNativeLoadFailureStatus(
                loadException,
                _accelerationPreference);
            _host?.Log(
                PluginLogLevel.Error,
                BuildAccelerationDiagnosticMessage(AccelerationDiagnostics));
            throw _accelerationPreference == TranscriptionAccelerationPreference.NvidiaCuda
                ? new InvalidOperationException(_accelerationStatus.Detail, loadException)
                : loadException;
        }

        var loadedLibrary = RuntimeOptions.LoadedLibrary;
        _accelerationStatus = CreateLoadedAccelerationStatus(
            loadedLibrary,
            _accelerationPreference,
            _gpuDevice);
        _customRocmRuntimeLoaded = _accelerationPreference == TranscriptionAccelerationPreference.AmdRocm;
        _runtimePath = ResolveRuntimePathForDiagnostics(
            loadedLibrary,
            _accelerationPreference,
            useRequestedBackend: false);
        _lastNativeError = null;
        _loadedModelId = modelId;
        _host?.Log(
            PluginLogLevel.Info,
            $"Loaded model {modelId}. {BuildAccelerationDiagnosticMessage(AccelerationDiagnostics)}{(_gpuDevice is { } gpu ? $" gpu='{gpu.Name}'" : "")}");
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
        await using var audioStream = new MemoryStream(wavAudio, writable: false);
        return await TranscribeCoreAsync(processor => processor.ProcessAsync(audioStream, ct), language, translate, prompt, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples, string? language, bool translate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var sample in samples.Span)
            if (!float.IsFinite(sample)) throw new ArgumentException("Audio samples must be finite.", nameof(samples));
        if (samples.IsEmpty) return Task.FromResult(new PluginTranscriptionResult("", language, 0, null));
        return TranscribeCoreAsync(processor => processor.ProcessAsync(samples, cancellationToken), language, translate, null, cancellationToken);
    }

    private async Task<PluginTranscriptionResult> TranscribeCoreAsync(
        Func<WhisperProcessor, IAsyncEnumerable<SegmentData>> process, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var modelId = _selectedModelId ?? throw new InvalidOperationException("Select a downloaded model before transcribing.");
            await LoadModelCoreAsync(modelId, ct).ConfigureAwait(false);

            var builder = _factory!.CreateBuilder()
                .WithLanguage(ResolveDecodeLanguage(modelId, language));

            if (!string.IsNullOrWhiteSpace(prompt))
                builder.WithPrompt(prompt);

            if (translate)
                builder.WithTranslate();

            using var processor = builder.Build();

            return await CollectResultAsync(process(processor), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string ResolveDecodeLanguage(string modelId, string? language) =>
        modelId.EndsWith(".en", StringComparison.Ordinal)
            ? "en"
            : string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim().ToLowerInvariant();

    internal static async Task<PluginTranscriptionResult> CollectResultAsync(
        IAsyncEnumerable<SegmentData> source, CancellationToken ct)
    {
        var text = new StringBuilder();
        var segments = new List<PluginTranscriptionSegment>();
        string? detectedLanguage = null;
        double durationSeconds = 0;
        float? noSpeechProbability = null;

        await foreach (var segment in source.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            // Whisper supplies its own whitespace, including no separator for CJK text.
            text.Append(segment.Text);
            if (!string.IsNullOrWhiteSpace(segment.Text))
                segments.Add(new(segment.Text.Trim(), segment.Start.TotalSeconds, segment.End.TotalSeconds));
            if (string.IsNullOrWhiteSpace(detectedLanguage) && !string.IsNullOrWhiteSpace(segment.Language))
                detectedLanguage = segment.Language;
            durationSeconds = Math.Max(durationSeconds, segment.End.TotalSeconds);
            var probability = segment.NoSpeechProbability;
            if (float.IsFinite(probability) && probability is >= 0 and <= 1)
                noSpeechProbability = noSpeechProbability is { } current ? Math.Min(current, probability) : probability;
        }

        return new PluginTranscriptionResult(text.ToString().Trim(), detectedLanguage, durationSeconds, noSpeechProbability)
        {
            Segments = segments
        };
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
        TranscriptionAccelerationPreference preference,
        GpuDevice? gpu = null)
    {
        if (preference == TranscriptionAccelerationPreference.AmdRocm)
            return new(TranscriptionAccelerationBackend.AmdRocm, "Using ROCm");

        return loadedLibrary switch
        {
            RuntimeLibrary.Cuda => new(TranscriptionAccelerationBackend.NvidiaCuda, "Using CUDA"),
            RuntimeLibrary.Vulkan => new(TranscriptionAccelerationBackend.AmdVulkan, "Using Vulkan",
                gpu is null ? null : $"Running on {gpu.Name}{(gpu.Integrated ? " (integrated graphics)" : "")}."),
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

    private async Task<bool> EnsureCudaRuntimeAvailableForLoadAsync(CancellationToken cancellationToken)
    {
        if (IsCudaRuntimeRestartRequired
            && _accelerationPreference is (TranscriptionAccelerationPreference.Auto or TranscriptionAccelerationPreference.NvidiaCuda))
        {
            _accelerationStatus = CreateCudaRuntimeInstalledRestartRequiredStatus();
            throw new InvalidOperationException(_accelerationStatus.Detail);
        }

        if (_accelerationPreference != TranscriptionAccelerationPreference.NvidiaCuda)
            return _accelerationPreference == TranscriptionAccelerationPreference.Auto
                && OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                && _cudaRuntimeInstaller is { } automaticInstaller
                && await automaticInstaller.VerifyInstalledAsync(cancellationToken).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            _accelerationStatus = CreateCudaRuntimeInstallFailureStatus(
                "NVIDIA CUDA acceleration for whisper.cpp is only available on Windows x64.");
            throw new InvalidOperationException(_accelerationStatus.Detail);
        }

        var installer = _cudaRuntimeInstaller
            ?? throw new InvalidOperationException("The whisper.cpp CUDA runtime installer is not available.");

        if (await installer.VerifyInstalledAsync(cancellationToken).ConfigureAwait(false))
            return true;

        var statusBeforeInstall = _accelerationStatus;
        _accelerationStatus = new(
            TranscriptionAccelerationBackend.Cpu,
            "Installing CUDA support",
            "Downloading the NVIDIA CUDA runtime needed for whisper.cpp.");
        _host?.Log(PluginLogLevel.Info, "Installing NVIDIA CUDA runtime for whisper.cpp.");

        bool installedNow;
        try
        {
            installedNow = await installer.EnsureInstalledAsync(cancellationToken);
            if (installedNow)
            {
                _cudaRuntimeRestartRequired = true;
                // AppDomain data survives collectible plugin instances but ends with the app process.
                // Store only a BCL value so the gate cannot retain a plugin load context.
                AppDomain.CurrentDomain.SetData(CudaRestartGateKey(installer.RuntimeDirectory), true);
                _accelerationStatus = CreateCudaRuntimeInstalledRestartRequiredStatus();
                _host?.NotifyCapabilitiesChanged();
            }
        }
        catch (OperationCanceledException)
        {
            _accelerationStatus = statusBeforeInstall;
            _host?.NotifyCapabilitiesChanged();
            throw;
        }
        catch (Exception ex)
        {
            _accelerationStatus = CreateCudaRuntimeInstallFailureStatus(
                "CUDA support download failed. " + ex.Message);
            throw new InvalidOperationException(_accelerationStatus.Detail, ex);
        }

        if (!await installer.VerifyInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            _accelerationStatus = CreateCudaRuntimeInstallFailureStatus(
                "CUDA support download completed, but the required NVIDIA runtime files are still missing.");
            throw new InvalidOperationException(_accelerationStatus.Detail);
        }

        if (!installedNow && !IsCudaRuntimeRestartRequired)
            return true;
        if (installedNow)
            _host?.Log(PluginLogLevel.Info,
                $"Installed NVIDIA CUDA runtime for whisper.cpp at {installer.RuntimeDirectory}.");
        _accelerationStatus = CreateCudaRuntimeInstalledRestartRequiredStatus();
        throw new InvalidOperationException(_accelerationStatus.Detail);
    }

    private void PrepareCudaRuntimeSearchPath(bool installationVerified)
    {
        var installer = _cudaRuntimeInstaller;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || _accelerationPreference is not (TranscriptionAccelerationPreference.Auto or TranscriptionAccelerationPreference.NvidiaCuda)
            || installer is null || !installationVerified || _host is null || _pluginDirectory is null)
            return;
        var cacheParent = Path.Join(_host.PluginAssetDirectory, "NativeRuntime");
        var cacheRoot = Path.Join(cacheParent, PluginVersion);
        StageCudaRuntime(_pluginDirectory, installer.RuntimeDirectory, cacheRoot);
        RemoveObsoleteNativeCaches(cacheParent, PluginVersion);
        // Whisper.net searches runtimes next to LibraryPath before its own assembly directory.
        RuntimeOptions.LibraryPath = Path.Join(cacheRoot, "whisper.dll");
    }

    internal static void RemoveObsoleteNativeCaches(string cacheParent, string currentVersion)
    {
        var root = Path.GetFullPath(cacheParent);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            var full = Path.GetFullPath(candidate);
            var name = Path.GetFileName(full);
            if (name == currentVersion || !Version.TryParse(name, out _) || Path.GetDirectoryName(full) != root) continue;
            try
            {
                var directories = new Stack<string>(); directories.Push(full);
                var files = new List<string>(); var safe = true;
                while (directories.Count > 0 && safe)
                {
                    var directory = directories.Pop();
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { safe = false; break; }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { safe = false; break; }
                        if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry); else files.Add(entry);
                    }
                }
                if (!safe) continue;
                // Loaded Windows DLLs reject exclusive write access. Leave their cache intact.
                foreach (var file in files)
                { using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                Directory.Delete(full, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine("Obsolete native cache remains in use: " + ex.GetType().Name); }
        }
    }

    internal static void StageCudaRuntime(string packageRoot, string cudaAssets, string cacheRoot)
    {
        var destination = Path.Join(cacheRoot, "runtimes", "cuda", "win-x64");
        var packagedRuntime = Path.Join(packageRoot, "runtimes", "cuda", "win-x64");
        Directory.CreateDirectory(destination);
        foreach (var source in Directory.EnumerateFiles(packagedRuntime, "*.dll")
            .Concat(Directory.EnumerateFiles(cudaAssets, "*.dll")))
        {
            var target = Path.Join(destination, Path.GetFileName(source));
            RemoveOrphanedStagingFiles(target);
            // Reuse intact loaded DLLs, but repair a damaged cache before native loading.
            if (!RuntimeFilesMatch(source, target))
            {
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, temporary);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { TryDeleteFile(temporary); }
            }
        }
    }

    private static bool RuntimeFilesMatch(string source, string target)
    {
        if (!File.Exists(target) || new FileInfo(source).Length != new FileInfo(target).Length) return false;
        using var original = File.OpenRead(source);
        using var cached = File.OpenRead(target);
        return SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(cached));
    }

    internal static void RemoveOrphanedModelDownloads(string modelPath)
        => RemoveOrphanedStagingFiles(modelPath);

    private static void RemoveOrphanedStagingFiles(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath)!;
        var prefix = Path.GetFileName(modelPath) + ".";
        foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
        {
            var name = Path.GetFileName(candidate);
            if (!Guid.TryParseExact(name[prefix.Length..^4], "N", out _)) continue;
            try
            {
                // An active writer rejects exclusive access. Delete only an orphan
                // we can open exclusively, and only for this exact target filename.
                using var orphan = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine("Temporary staging file is still in use or cannot be removed: " + ex.GetType().Name); }
        }
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
        var safeFileName = Path.GetFileName(model.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName is "." or "..")
            throw new InvalidOperationException("Model file name must not be empty.");

        return Path.Join(host.PluginAssetDirectory, "Models", safeFileName);
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
            if (current is DllNotFoundException or BadImageFormatException or SEHException)
                return true;

            if (current.Message.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("0x8007007E", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string BuildAccelerationDiagnosticMessage(
        TranscriptionAccelerationDiagnostics diagnostics)
    {
        var builder = new StringBuilder()
            .Append("engine=").Append(diagnostics.EngineId)
            .Append(" selected=").Append(diagnostics.SelectedPreference)
            .Append(" active=").Append(diagnostics.ActiveBackend);

        if (!string.IsNullOrWhiteSpace(diagnostics.RuntimePath))
            builder.Append(" runtime='").Append(diagnostics.RuntimePath).Append("'");
        if (!string.IsNullOrWhiteSpace(diagnostics.LastNativeError))
            builder.Append(" native-error='").Append(diagnostics.LastNativeError).Append("'");

        if (diagnostics.ActiveBackend == TranscriptionAccelerationBackend.NvidiaCuda)
        {
            builder.Append(
                " cuda-origin=unknown (native NVIDIA CUDA and CUDA translation layers such as ZLUDA are not distinguishable)");
        }

        return builder.ToString();
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

    private string? ResolveRuntimePathForDiagnostics(
        RuntimeLibrary? loadedLibrary,
        TranscriptionAccelerationPreference preference,
        bool useRequestedBackend)
    {
        if (preference == TranscriptionAccelerationPreference.AmdRocm)
            return ResolveRocmLibraryPathFromEnvironment();

        var effectiveLibrary = useRequestedBackend
            ? preference switch
            {
                TranscriptionAccelerationPreference.Cpu => RuntimeLibrary.Cpu,
                TranscriptionAccelerationPreference.NvidiaCuda => RuntimeLibrary.Cuda,
                TranscriptionAccelerationPreference.AmdVulkan => RuntimeLibrary.Vulkan,
                _ => loadedLibrary,
            }
            : loadedLibrary;
        if (effectiveLibrary is null || string.IsNullOrWhiteSpace(_pluginDirectory))
            return null;

        var runtimeIdentifier = Path.GetFileName(RuntimeInformation.RuntimeIdentifier);
        var runtimeDirectory = effectiveLibrary switch
        {
            RuntimeLibrary.Cuda => Path.Join(Path.GetDirectoryName(RuntimeOptions.LibraryPath) ?? _pluginDirectory, "runtimes", "cuda", runtimeIdentifier),
            RuntimeLibrary.Vulkan => Path.Join(_pluginDirectory, "runtimes", "vulkan", runtimeIdentifier),
            _ => Path.Join(_pluginDirectory, "runtimes", runtimeIdentifier),
        };
        var runtimePath = Path.Join(runtimeDirectory, "whisper.dll");
        return Path.GetFullPath(runtimePath);
    }

    private static string GetRootCauseMessage(Exception error)
    {
        var current = error;
        while (current.InnerException is not null)
            current = current.InnerException;

        return current.Message;
    }

    private void DisposeFactoryUnsafe()
    {
        if (_factory is { } factory) ReleaseFactory(factory);
        _factory = null;
        _loadedModelId = null;
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
        bool IsRecommended,
        long ExpectedSizeBytes);
}
