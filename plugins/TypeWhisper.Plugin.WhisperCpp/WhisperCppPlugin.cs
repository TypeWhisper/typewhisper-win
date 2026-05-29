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

public sealed class WhisperCppPlugin : ITypeWhisperPlugin, ITranscriptionEnginePlugin
{
    private const string CudaRuntimeDependencyHint =
        "Missing CUDA/cuBLAS runtime dependency cublas64_13.dll. TypeWhisper can download it when NVIDIA CUDA is selected.";
    private const string CudaFallbackDetail =
        "CUDA runtime could not be loaded; using CPU. " + CudaRuntimeDependencyHint;
    private const string CudaLoadFailureDetail =
        "CUDA runtime could not be loaded. " + CudaRuntimeDependencyHint;

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
    private TranscriptionAccelerationPreference _accelerationPreference = TranscriptionAccelerationPreference.Auto;
    private TranscriptionAccelerationStatus _accelerationStatus = new(
        TranscriptionAccelerationBackend.Cpu,
        "Using CPU");

    public WhisperCppPlugin()
    {
    }

    internal WhisperCppPlugin(IWhisperCppCudaRuntimeInstaller cudaRuntimeInstaller)
    {
        _cudaRuntimeInstaller = cudaRuntimeInstaller;
    }

    public string PluginId => "com.typewhisper.whisper-cpp";
    public string PluginName => "whisper.cpp (Local)";
    public string PluginVersion => "1.0.2";

    public string ProviderId => "whisper-cpp";
    public string ProviderDisplayName => "Local (whisper.cpp)";
    public bool IsConfigured => true;
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => true;
    public bool SupportsModelDownload => true;
    public IReadOnlyList<string> SupportedLanguages => [];
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
    [
        TranscriptionAccelerationBackend.Cpu,
        TranscriptionAccelerationBackend.NvidiaCuda
    ];
    public TranscriptionAccelerationPreference AccelerationPreference => _accelerationPreference;
    public TranscriptionAccelerationStatus AccelerationStatus => _accelerationStatus;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = Models.Select(model =>
        new PluginModelInfo(model.Id, model.DisplayName)
        {
            SizeDescription = model.SizeDescription,
            EstimatedSizeMB = model.EstimatedSizeMB,
            IsRecommended = model.IsRecommended,
            LanguageCount = model.LanguageCount,
        }).ToList();

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

    public async Task DeactivateAsync()
    {
        await UnloadModelAsync();
        _host = null;
        _pluginDirectory = null;
    }

    public UserControl? CreateSettingsView() => null;

    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        _accelerationPreference = preference;
        ApplyRuntimeLibraryOrder(preference);
        _accelerationStatus = CreatePendingAccelerationStatus(
            preference,
            _cudaRuntimeInstaller?.IsInstalled == true);
    }

    public void SelectModel(string modelId)
    {
        _ = GetModel(modelId);
        _selectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    public bool IsModelDownloaded(string modelId) => File.Exists(GetModelPath(modelId));

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

    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var modelPath = GetModelPath(modelId);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model files not found for: {modelId}", modelPath);

        await _gate.WaitAsync(ct);
        try
        {
            DisposeFactoryUnsafe();
            ApplyRuntimeLibraryOrder(_accelerationPreference);
            await EnsureCudaRuntimeAvailableForLoadAsync(ct);
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
            _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu]
        };

    private static void ApplyRuntimeLibraryOrder(TranscriptionAccelerationPreference preference) =>
        RuntimeOptions.RuntimeLibraryOrder = GetRuntimeLibraryOrder(preference).ToList();

    private static TranscriptionAccelerationStatus CreatePendingAccelerationStatus(
        TranscriptionAccelerationPreference preference,
        bool cudaRuntimeInstalled) =>
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
            _ => new(TranscriptionAccelerationBackend.Cpu, "Using CPU")
        };

    private static TranscriptionAccelerationStatus CreateLoadedAccelerationStatus(
        RuntimeLibrary? loadedLibrary,
        TranscriptionAccelerationPreference preference) =>
        loadedLibrary switch
        {
            RuntimeLibrary.Cuda => new(TranscriptionAccelerationBackend.NvidiaCuda, "Using CUDA"),
            RuntimeLibrary.Cpu => preference == TranscriptionAccelerationPreference.Auto
                ? new(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "CUDA runtime was not selected or could not be loaded.")
                : preference == TranscriptionAccelerationPreference.NvidiaCuda
                    ? new(
                        TranscriptionAccelerationBackend.Cpu,
                        "CUDA unavailable",
                        CudaFallbackDetail)
                : new(TranscriptionAccelerationBackend.Cpu, "Using CPU"),
            _ => new(TranscriptionAccelerationBackend.Cpu, "Using CPU")
        };

    private static TranscriptionAccelerationStatus CreateNativeLoadFailureStatus(
        Exception error,
        TranscriptionAccelerationPreference preference) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            preference == TranscriptionAccelerationPreference.NvidiaCuda
                ? "CUDA unavailable"
                : "Native runtime unavailable",
            preference == TranscriptionAccelerationPreference.NvidiaCuda
                ? CudaLoadFailureDetail
                : error.Message);

    private async Task EnsureCudaRuntimeAvailableForLoadAsync(CancellationToken cancellationToken)
    {
        if (_accelerationPreference != TranscriptionAccelerationPreference.NvidiaCuda)
            return;

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
    }

    private static TranscriptionAccelerationStatus CreateCudaRuntimeInstallFailureStatus(string detail) =>
        new(
            TranscriptionAccelerationBackend.Cpu,
            "CUDA unavailable",
            detail);

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
        const string requiredFiles =
            "whisper.dll, ggml-whisper.dll, ggml-base-whisper.dll, ggml-cpu-whisper.dll, " +
            "ggml-cuda-whisper.dll (for CUDA), cublas64_13.dll (for CUDA/cuBLAS), " +
            "msvcp140.dll, vcruntime140.dll, " +
            "vcruntime140_1.dll, VCOMP140.DLL";

        return "Unable to load the whisper.cpp native runtime. " +
            $"Expected CPU native DLLs under '{runtimeDirectory}' and CUDA native DLLs under " +
            $"'{cudaRuntimeDirectory}', including {requiredFiles}. " +
            CudaRuntimeDependencyHint + " " +
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
