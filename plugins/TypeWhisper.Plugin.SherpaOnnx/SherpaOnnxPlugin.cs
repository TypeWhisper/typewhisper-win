using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SherpaOnnx;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
/// Runs NVIDIA Parakeet and Canary locally: on the CPU through sherpa-onnx, and Parakeet on any Vulkan graphics card
/// through transcribe.cpp, whose runtime and GGUF model are downloaded when the GPU is first chosen.
/// </summary>
public sealed class SherpaOnnxPlugin : ITypeWhisperPlugin, IPcmTranscriptionEnginePlugin
{
    internal const int SampleRate = 16000;
    // The exported encoder fails near 400 seconds; use a lower bound to leave room for feature padding.
    internal const int ParakeetMaximumChunkSeconds = 300;
    internal const int ParakeetChunkOverlapSeconds = 5;
    // Matches the macOS Canary plugin: 20-second chunks cut at the quietest 100 ms within 5 seconds.
    internal const int CanaryChunkSeconds = 20;
    internal const int CanaryChunkSearchSeconds = 5;
    internal const int CanarySilenceWindowMilliseconds = 100;
    private const int MinimumTranscriptOverlapWords = 2;
    private const int MaximumTranscriptOverlapWords = 40;
    // The NVIDIA models are csukuangfj's sherpa-onnx exports on Hugging Face. The URLs name a commit
    // instead of the main branch because the files are parsed by native code and main can serve
    // different bytes under the same address; each file is pinned to the SHA-256 it had in that
    // commit as well. Pinned on 2026-10-08 to the newest commit of each repository: the SHA-256 values
    // are the LFS object ids from https://huggingface.co/api/models/<repo>/tree/<commit>, and
    // tokens.txt, which is not stored in LFS, was hashed from a download of that commit.
    // Commit 2bda32ec of 2025-08-16.
    private const string ParakeetRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78";
    // Our own export of moondream/parakeet-ultra with the sherpa-onnx v3 script; the files are pinned by hash.
    private const string ParakeetUltraRepo = "https://github.com/TypeWhisper/typewhisper-win/releases/download/model-parakeet-ultra-int8-v1";
    // Commit 9077164e of 2025-07-07.
    private const string CanaryRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/9077164e0d3dd1d5353743e89ceaa1d3a770838c";
    // A download is verified under this suffix and only renamed to its final name once its hash matches.
    private const string UnverifiedSuffix = ".unverified";

    private static readonly IReadOnlyList<string> CanarySupportedLanguages = ["en", "de", "fr", "es"];
    // NVIDIA model card: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3
    private static readonly IReadOnlyList<string> ParakeetSupportedLanguages =
        ["bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk"];
    private static readonly Regex TranscriptWordRegex = new(
        @"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static readonly IReadOnlyList<ModelDefinition> DefaultModels =
    [
        new("parakeet-ultra-0.6b", "Parakeet Ultra 0.6B", "Moondream", "~670 MB", 670, 25, true, false,
        [
            new("encoder.int8.onnx", $"{ParakeetUltraRepo}/encoder.int8.onnx", 652, "b42621d3c0f40fe0236a629fd6c3fad13575e5f9d22ed1a5addba14091931211"),
            new("decoder.int8.onnx", $"{ParakeetUltraRepo}/decoder.int8.onnx", 12, "1fab98fe6c12aded87d2da66272cc9e148d0a0044ce3850a12fe56302ec4a922"),
            new("joiner.int8.onnx", $"{ParakeetUltraRepo}/joiner.int8.onnx", 6, "8a71aaccfdba3d451775507a889afd2257d2e754463fd81095a8c8f290f924c2"),
            new("tokens.txt", $"{ParakeetUltraRepo}/tokens.txt", 1, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d")
        ], TranscribeCppAsset.ParakeetUltraQ8),
        new("parakeet-tdt-0.6b", "Parakeet TDT 0.6B", "NVIDIA", "~670 MB", 670, 25, false, false,
        [
            new("encoder.int8.onnx", $"{ParakeetRepo}/encoder.int8.onnx", 652, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
            new("decoder.int8.onnx", $"{ParakeetRepo}/decoder.int8.onnx", 12, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
            new("joiner.int8.onnx", $"{ParakeetRepo}/joiner.int8.onnx", 6, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
            new("tokens.txt", $"{ParakeetRepo}/tokens.txt", 1, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d")
        ], TranscribeCppAsset.ParakeetV3Q8),
        new("canary-180m-flash", "Canary 180M Flash", "NVIDIA", "~198 MB", 198, 4, false, true,
        [
            new("encoder.int8.onnx", $"{CanaryRepo}/encoder.int8.onnx", 127, "7a75b4e2a5857a6dcc0819503bbe3fad66943db4a3ccf21d3f27c633667d303f"),
            new("decoder.int8.onnx", $"{CanaryRepo}/decoder.int8.onnx", 71, "e41a2ab9c0c2fe81a1e8ade5a45fb02a74bc4db7d1f91b89a54a25e2cf79cba2"),
            new("tokens.txt", $"{CanaryRepo}/tokens.txt", 1, "2dae6fc7815f9640645e0c765522b278ee0cef49b482d91f6913e334628d3e77")
        ])
    ];

    private readonly object _sync = new();
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<ModelDefinition> _models;
    internal Func<string, long?> AvailableBytes { get; set; } = ModelStorageSpace.GetAvailableBytes;
    private readonly Func<string, string, OfflineRecognizer>? _recognizerFactory;
    private readonly Func<string, IGpuRecognizer>? _gpuFactory;
    private readonly TranscribeCppAssetStore _gpuRuntime;
    private IPluginHostServices? _host;
    private OfflineRecognizer? _recognizer;
    private IGpuRecognizer? _gpu;
    private string? _loadedModelId;
    private string? _loadedModelDir;
    private string? _selectedModelId;
    private TranscriptionAccelerationPreference _accelerationPreference = TranscriptionAccelerationPreference.Auto;
    private TranscriptionAccelerationStatus _accelerationStatus = new(
        TranscriptionAccelerationBackend.Cpu,
        "Using CPU");

    /// <summary>
    /// Initializes a new instance of the SherpaOnnxPlugin class.
    /// </summary>
    public SherpaOnnxPlugin()
        : this(httpClient: null)
    {
    }

    // Tests download through a fake handler and a small catalog whose hashes they control, and replace the native
    // recognizers: the CPU factory receives model id and directory, the GPU factory the GGUF path.
    internal SherpaOnnxPlugin(
        HttpClient? httpClient,
        IReadOnlyList<ModelDefinition>? models = null,
        Func<string, string, OfflineRecognizer>? recognizerFactory = null,
        Func<string, IGpuRecognizer>? gpuFactory = null,
        TranscribeCppAsset? gpuRuntime = null)
    {
        _recognizerFactory = recognizerFactory;
        _gpuFactory = gpuFactory;
        _httpClient = httpClient ?? new HttpClient();
        _gpuRuntime = new(_httpClient, gpuRuntime ?? TranscribeCppAsset.Runtime, path => AvailableBytes(path));
        _models = models ?? DefaultModels;
        TranscriptionModels = _models.Select(m =>
            new PluginModelInfo(m.Id, m.DisplayName)
            {
                Publisher = m.Publisher,
                SizeDescription = m.SizeDescription,
                EstimatedSizeMB = m.EstimatedSizeMB,
                IsRecommended = m.IsRecommended,
                LanguageCount = m.LanguageCount,
                LanguageCodes = m.Id == "canary-180m-flash" ? CanarySupportedLanguages : ParakeetSupportedLanguages,
            }).ToList();
    }

    // Canary-specific state
    private string _canarySrcLang = "en";
    private string _canaryTgtLang = "en";

    // ITypeWhisperPlugin
    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.sherpa-onnx";
    /// <summary>
    /// Performs modelle.
    /// </summary>
    public string PluginName => "NVIDIA Parakeet";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.3.0";

    // ITranscriptionEnginePlugin
    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "sherpa-onnx";
    /// <summary>
    /// Gets the provider display name.
    /// </summary>
    public string ProviderDisplayName => "NVIDIA Parakeet";
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    public bool IsConfigured => true;
    /// <summary>
    /// Gets the currently selected provider model identifier.
    /// </summary>
    public string? SelectedModelId => _selectedModelId;
    /// <summary>Local PCM inference can provide interim snapshots.</summary>
    public bool SupportsLocalLivePreview => true;

    /// <summary>Whether the selected model can translate audio.</summary>
    public bool SupportsTranslation => _selectedModelId == "canary-180m-flash";
    /// <summary>
    /// Gets whether the provider can download models through the host.
    /// </summary>
    public bool SupportsModelDownload => true;
    /// <summary>
    /// Gets whether downloaded model directories can be removed.
    /// </summary>
    public bool SupportsModelRemoval => true;
    /// <summary>
    /// The CPU everywhere, and on Windows x64 any graphics card with Vulkan through transcribe.cpp. The host labels
    /// <see cref="TranscriptionAccelerationBackend.AmdVulkan"/> as its Vulkan option; it is not limited to AMD.
    /// </summary>
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } = GpuPlatform
        ? [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.AmdVulkan]
        : [TranscriptionAccelerationBackend.Cpu];

    // transcribe.cpp publishes its Vulkan runtime for Windows x64 only.
    internal static bool GpuPlatform => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
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
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }

    /// <summary>
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages =>
        _selectedModelId == "canary-180m-flash" ? CanarySupportedLanguages : [];

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        SherpaOnnxNativeRuntime.RegisterResolver();
        if (host.AllowLegacyDataMigration) MigrateModelFiles();
        RemoveRetiredCudaRuntime(host.PluginAssetDirectory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes the NVIDIA CUDA runtime that plugin versions before 1.3.0 downloaded (about 2.3 GB). The GPU now runs
    /// through Vulkan, so nothing loads it any more. Files an older process still holds are left for the next start.
    /// </summary>
    internal static void RemoveRetiredCudaRuntime(string pluginAssetDirectory)
    {
        var directory = Path.Join(pluginAssetDirectory, "Runtimes", "sherpa-onnx-cuda");
        try
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        UnloadRecognizer();
        return Task.CompletedTask;
    }


    /// <summary>
    /// Sets acceleration preference; it takes effect on the next model load. A NVIDIA CUDA choice saved for an earlier
    /// version reads as Automatic, so the update does not download the graphics card files unasked.
    /// </summary>
    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        _accelerationPreference = preference switch
        {
            TranscriptionAccelerationPreference.Cpu => preference,
            TranscriptionAccelerationPreference.AmdVulkan when GpuPlatform => preference,
            _ => TranscriptionAccelerationPreference.Auto
        };
        if (_loadedModelId is null) _accelerationStatus = CreatePendingAccelerationStatus(_accelerationPreference);
    }

    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        _selectedModelId = modelId;
    }

    /// <summary>
    /// Whether the model can be used with the current processing device: its CPU files are present, or, unless the CPU
    /// was chosen, its GPU files and the Vulkan runtime are.
    /// </summary>
    public bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        return IsCpuReady(model) || (_accelerationPreference != TranscriptionAccelerationPreference.Cpu && IsGpuReady(model));
    }

    private bool IsCpuReady(ModelDefinition model)
    {
        var dir = GetModelDirectory(model.Id);
        return model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName)) && new FileInfo(Path.Combine(dir, f.FileName)).Length > 0);
    }

    private bool IsGpuReady(ModelDefinition model) => GpuPlatform && model.Gguf is { } gguf
        && _gpuRuntime.IsReady(GpuRuntimeDirectory) && GpuModelStore(gguf).IsReady(GpuModelDirectory(model.Id));

    // With the GPU chosen, a download fetches what the GPU runs; otherwise the CPU files.
    private bool DownloadsForGpu(ModelDefinition model) =>
        GpuPlatform && model.Gguf is not null && _accelerationPreference == TranscriptionAccelerationPreference.AmdVulkan;

    /// <summary>
    /// Downloads the requested model for the current processing device and reports progress when available.
    /// </summary>
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        if (DownloadsForGpu(model))
        {
            await DownloadGpuFilesAsync(model, progress, ct);
            return;
        }
        var dir = GetModelDirectory(modelId);
        Directory.CreateDirectory(dir);

        var missing = model.Files.Where(f => !File.Exists(Path.Join(dir, f.FileName)) || new FileInfo(Path.Join(dir, f.FileName)).Length == 0).ToList();
        foreach (var file in missing)
        {
            // ".tmp" is the downloader's partial file (also what plugin 1.2.0 left behind), ".unverified"
            // a complete download that was never checked; neither counts as the model file.
            var path = Path.Join(dir, file.FileName);
            foreach (var abandoned in new[] { path + ".tmp", path + UnverifiedSuffix + ".tmp", path + UnverifiedSuffix })
                ModelStorageSpace.TryRemoveAbandonedFile(abandoned);
        }
        ModelStorageSpace.EnsureAvailable(dir, missing.Sum(f => f.EstimatedSizeMB * 1024L * 1024), model.DisplayName, AvailableBytes);

        var total = model.Files.Sum(f => f.EstimatedSizeMB);
        double completed = 0;
        foreach (var file in model.Files)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(dir, file.FileName);
            var start = completed;
            if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
            {
                // Files already at the destination are kept as they are, whichever plugin version
                // downloaded them; only what is downloaded now is verified. The download lands next
                // to the destination and is renamed only after its hash matched, so that
                // IsModelDownloaded and LoadModelAsync, which accept anything at the final name,
                // never see a file that failed or skipped the check.
                var unverified = destination + UnverifiedSuffix;
                await TypeWhisper.PluginSDK.Helpers.ModelFileDownloader.DownloadAsync(_httpClient, file.DownloadUrl, unverified,
                    new DownloadProgress(value => progress?.Report((start + value * file.EstimatedSizeMB) / total)), ct);
                await VerifyChecksumAsync(file.FileName, file.Sha256, unverified, ct);
                File.Move(unverified, destination, overwrite: true);
            }
            completed += file.EstimatedSizeMB;
            progress?.Report(completed / total);
        }
    }

    // The Vulkan runtime is shared by every model; each model keeps its GGUF next to its CPU files, so removing the
    // model removes both.
    private async Task DownloadGpuFilesAsync(ModelDefinition model, IProgress<double>? progress, CancellationToken ct)
    {
        var store = GpuModelStore(model.Gguf!);
        var runtimeSize = _gpuRuntime.IsReady(GpuRuntimeDirectory) ? 0 : _gpuRuntime.Asset.Size;
        double total = runtimeSize + model.Gguf!.Size;
        await _gpuRuntime.DownloadAsync(GpuRuntimeDirectory,
            new DownloadProgress(value => progress?.Report(value * runtimeSize / total)), ct);
        await store.DownloadAsync(GpuModelDirectory(model.Id),
            new DownloadProgress(value => progress?.Report((runtimeSize + value * model.Gguf.Size) / total)), ct);
    }

    private TranscribeCppAssetStore GpuModelStore(TranscribeCppAsset gguf) => new(_httpClient, gguf, path => AvailableBytes(path));

    private string GpuRuntimeDirectory =>
        Path.Join(_host?.PluginAssetDirectory ?? ".", "Runtimes", "transcribe-cpp", TranscribeCppNative.Version + "-cpu-vulkan");

    private string GpuModelDirectory(string modelId) => Path.Join(GetModelDirectory(modelId), "gpu");

    internal static async Task VerifyChecksumAsync(string fileName, string sha256, string path, CancellationToken ct)
    {
        string actual;
        await using (var stream = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            return;

        File.Delete(path);
        throw new InvalidDataException($"The downloaded {fileName} does not match its expected checksum.");
    }

    private sealed class DownloadProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
    /// <summary>
    /// Removes the downloaded files for the requested model.
    /// </summary>
    public Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        _ = GetModelDefinition(modelId);
        var directory = GetModelDirectory(modelId);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (string.Equals(_loadedModelId, modelId, StringComparison.Ordinal))
                UnloadRecognizerUnsafe();

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases the active recognizer without disabling the plugin.
    /// </summary>
    public Task UnloadModelAsync()
    {
        UnloadRecognizer();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads the model on the chosen processing device. The GPU downloads its runtime and model on first use and runs
    /// the model on the first dedicated graphics card with Vulkan, or on integrated graphics when there is none.
    /// Automatic uses the GPU once its files are present and falls back to the CPU when the GPU cannot load.
    /// </summary>
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        var preference = _accelerationPreference;
        var useGpu = GpuPlatform && model.Gguf is not null && preference switch
        {
            TranscriptionAccelerationPreference.AmdVulkan => true,
            TranscriptionAccelerationPreference.Auto => IsGpuReady(model),
            _ => false
        };
        if (useGpu && !IsGpuReady(model))
            await DownloadGpuFilesAsync(model, null, ct);
        if (!useGpu && !IsCpuReady(model))
            throw new FileNotFoundException($"Model files not found for: {modelId}");

        await Task.Run(() =>
        {
            lock (_sync)
            {
                UnloadRecognizerUnsafe();
                TranscriptionAccelerationStatus status;
                if (useGpu)
                {
                    try
                    {
                        _gpu = CreateGpuRecognizer(Path.Join(GpuModelDirectory(modelId), model.Gguf!.FileName));
                        status = _gpu.Status;
                    }
                    catch (Exception ex) when (preference == TranscriptionAccelerationPreference.Auto && IsCpuReady(model)
                        && ex is not OperationCanceledException)
                    {
                        _host?.Log(PluginLogLevel.Warning, $"The GPU could not load {modelId}; falling back to the CPU: {ex.Message}");
                        status = new(TranscriptionAccelerationBackend.Cpu, "Using CPU", "GPU unavailable: " + ex.Message);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _accelerationStatus = new(TranscriptionAccelerationBackend.Cpu, "GPU unavailable", ex.Message);
                        throw;
                    }
                }
                else
                {
                    status = preference == TranscriptionAccelerationPreference.AmdVulkan
                        ? new(TranscriptionAccelerationBackend.Cpu, "Using CPU", $"{model.DisplayName} runs on the CPU.")
                        : new(TranscriptionAccelerationBackend.Cpu, "Using CPU");
                }
                if (_gpu is null) _recognizer = CreateRecognizerForLoad(model, dir);

                _loadedModelId = modelId;
                _loadedModelDir = dir;
                _selectedModelId = modelId;
                _canarySrcLang = "en";
                _canaryTgtLang = "en";
                _accelerationStatus = status;

                _host?.Log(PluginLogLevel.Info, $"Loaded model {modelId} ({_accelerationStatus.DisplayText})");
                Debug.WriteLine($"[SherpaOnnx] Model {modelId} loaded from {dir} ({_accelerationStatus.DisplayText})");
            }
        }, ct);
    }

    private IGpuRecognizer CreateGpuRecognizer(string ggufPath)
    {
        if (_gpuFactory is not null) return _gpuFactory(ggufPath);
        var host = _host;
        TranscribeCppRuntime.Initialize(GpuRuntimeDirectory, TranscribeCppNative.BackendMaskCpu | TranscribeCppNative.BackendMaskVulkan,
            (level, message) =>
            {
                // 2 = WARN, 3 = ERROR; INFO and DEBUG would flood the log with per-run decoder statistics.
                if (level is 2 or 3 && message.Length > 0)
                    host?.Log(level == 3 ? PluginLogLevel.Error : PluginLogLevel.Warning, "transcribe.cpp: " + message);
            });
        var device = TranscribeCppRuntime.PreferredVulkanDevice(TranscribeCppRuntime.Devices())
            ?? throw new InvalidOperationException("No graphics card with Vulkan support was found. Update the graphics driver or choose CPU.");
        return new TranscribeCppRecognizer(TranscribeCppSession.Open(ggufPath, TranscribeCppNative.BackendVulkan, device,
            Math.Max(1, Environment.ProcessorCount / 2)));
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
        => await TranscribePcmAsync(DecodeWav(wavAudio), language, translate, ct);

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribePcmAsync(
        ReadOnlyMemory<float> samples, string? language, bool translate, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var audioSamples = samples.ToArray();
            var audioDuration = audioSamples.Length / (double)SampleRate;

            lock (_sync)
            {
                if (_gpu is not null && _loadedModelId is not null)
                    return TranscribeOnGpu(_gpu, audioSamples, cancellationToken);
                if (_recognizer is null || _loadedModelId is null)
                    throw new InvalidOperationException("Kein Modell geladen. LoadModelAsync zuerst aufrufen.");

                var model = GetModelDefinition(_loadedModelId);

                if (model.SupportsTranslation)
                {
                    EnsureCanaryLanguage(language, translate);
                    var (canaryText, detectedLanguage) = TranscribeCanarySamples(_recognizer, audioSamples, cancellationToken);
                    return new PluginTranscriptionResult(canaryText, detectedLanguage, audioDuration, NoSpeechProbability: null);
                }

                // Long recordings need bounded chunks; single-chunk recordings retain token timings.
                if (audioSamples.Length > ParakeetMaximumChunkSeconds * SampleRate)
                {
                    var recognizer = _recognizer;
                    return new PluginTranscriptionResult(TranscribeParakeetSamples(chunk => RecognizeSamples(recognizer, chunk), audioSamples,
                        cancellationToken), null, audioDuration, NoSpeechProbability: null);
                }

                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(SampleRate, audioSamples);
                _recognizer.Decode(stream);
                var result = stream.Result;

                return new PluginTranscriptionResult(result.Text.Trim(), null, audioDuration, NoSpeechProbability: null)
                {
                    TokenTimings = result.Tokens is not null && result.Timestamps is not null
                        ? TypeWhisper.PluginSDK.Helpers.TranscriptionTokenTimings.Create(result.Tokens, result.Timestamps, result.Durations, audioDuration) : []
                };
            }
        }, cancellationToken);
    }

    // The GPU uses the same chunks as the CPU: one pass over a long recording would hold its whole attention on the
    // graphics card. Token timings, which dictionary boosting needs, come from single-chunk recordings.
    private PluginTranscriptionResult TranscribeOnGpu(IGpuRecognizer gpu, float[] audioSamples, CancellationToken ct)
    {
        var duration = audioSamples.Length / (double)SampleRate;
        if (audioSamples.Length == 0) return new(string.Empty, null, 0, NoSpeechProbability: null);
        if (audioSamples.Length > ParakeetMaximumChunkSeconds * SampleRate)
            return new(TranscribeParakeetSamples(chunk => gpu.Transcribe(chunk, tokens: false, ct).Text, audioSamples, ct),
                null, duration, NoSpeechProbability: null);
        var result = gpu.Transcribe(audioSamples, tokens: true, ct);
        return new(result.Text, null, duration, NoSpeechProbability: null) { TokenTimings = TokenTimings(result.Tokens, duration) };
    }

    internal static VocabularyTokenTiming[] TokenTimings(IReadOnlyList<TranscribeCppToken> tokens, double duration) =>
        TranscriptionTokenTimings.Create(tokens.Select(token => token.Text).ToArray(),
            tokens.Select(token => (float)token.StartSeconds).ToArray(),
            tokens.Select(token => (float)(token.EndSeconds - token.StartSeconds)).ToArray(), duration);

    private string TranscribeParakeetSamples(
        Func<float[], string> recognize,
        float[] audioSamples,
        CancellationToken ct)
    {
        var chunks = CreateParakeetChunks(audioSamples.Length);
        if (chunks.Count == 1)
            return recognize(audioSamples);

        _host?.Log(
            PluginLogLevel.Info,
            $"Splitting {audioSamples.Length / (double)SampleRate:F1}s of Parakeet audio into {chunks.Count} chunks.");

        var transcript = string.Empty;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var samples = new float[chunk.Count];
            Array.Copy(audioSamples, chunk.Offset, samples, 0, chunk.Count);
            transcript = MergeChunkTranscripts(transcript, recognize(samples));
        }

        return transcript;
    }

    private (string Text, string? DetectedLanguage) TranscribeCanarySamples(
        OfflineRecognizer recognizer,
        float[] audioSamples,
        CancellationToken ct)
    {
        var chunks = CreateCanaryChunks(audioSamples);
        if (chunks.Count > 1)
            _host?.Log(
                PluginLogLevel.Info,
                $"Splitting {audioSamples.Length / (double)SampleRate:F1}s of Canary audio into {chunks.Count} chunks.");

        var transcript = string.Empty;
        string? detectedLanguage = null;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var samples = chunks.Count == 1 ? audioSamples : audioSamples[chunk.Offset..(chunk.Offset + chunk.Count)];
            var (text, language) = ParseCanaryResult(RecognizeSamples(recognizer, samples));
            // Chunks do not overlap, so joining must not drop words that repeat across a boundary.
            transcript = transcript.Length == 0 ? text : AppendTranscript(transcript, text);
            detectedLanguage ??= language;
        }

        return (transcript, detectedLanguage);
    }

    /// <summary>
    /// Splits audio for Canary, which was trained on short segments and silently drops or repeats
    /// speech in longer input. Each cut lands in the quietest window near the target length.
    /// </summary>
    internal static IReadOnlyList<(int Offset, int Count)> CreateCanaryChunks(ReadOnlySpan<float> samples)
    {
        var chunkSamples = CanaryChunkSeconds * SampleRate;
        var searchSamples = CanaryChunkSearchSeconds * SampleRate;
        var halfWindow = SampleRate * CanarySilenceWindowMilliseconds / 2000;
        var step = SampleRate / 100;
        var chunks = new List<(int Offset, int Count)>();
        var start = 0;

        while (samples.Length - start > chunkSamples)
        {
            var target = start + chunkSamples;
            // Keep at least one second on both sides of every cut.
            var searchStart = Math.Max(start + SampleRate, target - searchSamples);
            var searchEnd = Math.Min(samples.Length - SampleRate, target + searchSamples);
            var cut = target;
            var quietest = double.MaxValue;

            for (var candidate = searchStart; candidate <= searchEnd; candidate += step)
            {
                var energy = 0d;
                foreach (var sample in samples[(candidate - halfWindow)..(candidate + halfWindow)])
                    energy += sample * sample;
                if (energy < quietest)
                {
                    quietest = energy;
                    cut = candidate;
                }
            }

            chunks.Add((start, cut - start));
            start = cut;
        }

        chunks.Add((start, samples.Length - start));
        return chunks;
    }

    private static string RecognizeSamples(OfflineRecognizer recognizer, float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    internal static IReadOnlyList<(int Offset, int Count)> CreateParakeetChunks(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        var maximumChunkSamples = ParakeetMaximumChunkSeconds * SampleRate;
        if (sampleCount <= maximumChunkSamples)
            return [(0, sampleCount)];

        var overlapSamples = ParakeetChunkOverlapSeconds * SampleRate;
        var strideSamples = maximumChunkSamples - overlapSamples;
        var chunkCount = (int)Math.Ceiling(
            (sampleCount - overlapSamples) / (double)strideSamples);
        var processedSampleCount = sampleCount + ((chunkCount - 1) * overlapSamples);
        var baseChunkSize = processedSampleCount / chunkCount;
        var longerChunkCount = processedSampleCount % chunkCount;
        var chunks = new List<(int Offset, int Count)>(chunkCount);
        var offset = 0;

        for (var index = 0; index < chunkCount; index++)
        {
            var count = baseChunkSize + (index < longerChunkCount ? 1 : 0);
            chunks.Add((offset, count));
            offset += count - overlapSamples;
        }

        return chunks;
    }

    internal static string MergeChunkTranscripts(string transcript, string nextChunk)
    {
        transcript = transcript.Trim();
        nextChunk = nextChunk.Trim();
        if (string.IsNullOrEmpty(transcript))
            return nextChunk;
        if (string.IsNullOrEmpty(nextChunk))
            return transcript;

        var transcriptWords = TokenizeTranscriptWords(transcript);
        var nextWords = TokenizeTranscriptWords(nextChunk);
        var maximumOverlap = Math.Min(
            MaximumTranscriptOverlapWords,
            Math.Min(transcriptWords.Count, nextWords.Count));

        for (var overlap = maximumOverlap; overlap >= MinimumTranscriptOverlapWords; overlap--)
        {
            var transcriptStart = transcriptWords.Count - overlap;
            var matches = true;
            for (var index = 0; index < overlap; index++)
            {
                if (!string.Equals(
                        transcriptWords[transcriptStart + index].Normalized,
                        nextWords[index].Normalized,
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return AppendTranscript(transcript, nextChunk[nextWords[overlap - 1].End..]);
        }

        return AppendTranscript(transcript, nextChunk);
    }

    private static List<TranscriptWord> TokenizeTranscriptWords(string text) =>
        TranscriptWordRegex.Matches(text)
            .Select(match => new TranscriptWord(
                match.Value.Replace('’', '\'').ToUpperInvariant(),
                match.Index + match.Length))
            .ToList();

    private static string AppendTranscript(string transcript, string tail)
    {
        tail = tail.TrimStart();
        if (string.IsNullOrEmpty(tail))
            return transcript;

        var firstCharacter = tail[0];
        var punctuationCategory = char.GetUnicodeCategory(firstCharacter);
        var isOpeningPunctuation = punctuationCategory is UnicodeCategory.OpenPunctuation
            or UnicodeCategory.InitialQuotePunctuation
            || ((firstCharacter is '"' or '\'')
                && tail.Length > 1
                && !char.IsWhiteSpace(tail[1]));

        if (char.IsPunctuation(firstCharacter) && !isOpeningPunctuation)
        {
            var transcriptEnd = transcript.Length;
            while (transcriptEnd > 0 && char.IsPunctuation(transcript[transcriptEnd - 1]))
                transcriptEnd--;

            return transcript[..transcriptEnd] + tail;
        }

        return transcript + " " + tail;
    }

    private sealed record TranscriptWord(string Normalized, int End);

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        UnloadRecognizer();
        _httpClient.Dispose();
    }

    // --- Private helpers ---

    private static TranscriptionAccelerationStatus CreatePendingAccelerationStatus(TranscriptionAccelerationPreference preference) =>
        preference == TranscriptionAccelerationPreference.AmdVulkan
            ? new(TranscriptionAccelerationBackend.AmdVulkan, "Using GPU")
            : new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    private string GetModelDirectory(string modelId)
    {
        var safeModelId = Path.GetFileName(modelId);
        if (string.IsNullOrWhiteSpace(safeModelId) || safeModelId is "." or "..")
            throw new ArgumentException("Model ID must not be empty.", nameof(modelId));

        return Path.Join(_host?.PluginAssetDirectory ?? ".", "Models", safeModelId);
    }

    private ModelDefinition GetModelDefinition(string modelId) =>
        _models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void UnloadRecognizer()
    {
        lock (_sync)
            UnloadRecognizerUnsafe();
    }

    private void UnloadRecognizerUnsafe()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _gpu?.Dispose();
        _gpu = null;
        _loadedModelId = null;
        _loadedModelDir = null;
        _canarySrcLang = "en";
        _canaryTgtLang = "en";
    }

    internal static OfflineRecognizerConfig CreateParakeetConfig(string modelDir)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return config;
    }

    private static OfflineRecognizer CreateRecognizer(ModelDefinition model, string modelDir) =>
        model.SupportsTranslation
            ? CreateCanaryRecognizer(modelDir, "en", "en")
            : new(CreateParakeetConfig(modelDir));

    private OfflineRecognizer CreateRecognizerForLoad(ModelDefinition model, string modelDir) =>
        _recognizerFactory is null
            ? CreateRecognizer(model, modelDir)
            : _recognizerFactory(model.Id, modelDir);

    internal static OfflineRecognizerConfig CreateCanaryConfig(
        string modelDir,
        string srcLang,
        string tgtLang)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Canary.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Canary.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Canary.SrcLang = srcLang;
        config.ModelConfig.Canary.TgtLang = tgtLang;
        config.ModelConfig.Canary.UsePnc = 1;
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return config;
    }

    private static OfflineRecognizer CreateCanaryRecognizer(
        string modelDir,
        string srcLang,
        string tgtLang) =>
        new(CreateCanaryConfig(modelDir, srcLang, tgtLang));

    private void EnsureCanaryLanguage(string? language, bool translate)
    {
        if (_loadedModelDir is null) return;

        var srcLang = NormalizeCanaryLanguage(language);
        var tgtLang = translate ? "en" : srcLang;

        if (srcLang == _canarySrcLang && tgtLang == _canaryTgtLang) return;

        _recognizer?.Dispose();
        _recognizer = CreateCanaryRecognizer(_loadedModelDir, srcLang, tgtLang);
        _canarySrcLang = srcLang;
        _canaryTgtLang = tgtLang;
    }

    private static string NormalizeCanaryLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto")
            return "en";
        var normalized = language.Trim().ToLowerInvariant();
        return CanarySupportedLanguages.Contains(normalized) ? normalized : "en";
    }

    private static (string Text, string? DetectedLanguage) ParseCanaryResult(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return (string.Empty, null);

        try
        {
            using var json = JsonDocument.Parse(rawText);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return (rawText.Trim(), null);

            var text = rawText.Trim();
            if (json.RootElement.TryGetProperty("text", out var textNode))
                text = textNode.GetString()?.Trim() ?? string.Empty;

            string? lang = null;
            if (json.RootElement.TryGetProperty("lang", out var langNode))
            {
                var parsed = langNode.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                    lang = parsed;
            }

            return (text, lang);
        }
        catch (JsonException)
        {
            return (rawText.Trim(), null);
        }
    }

    private static float[] DecodeWav(byte[] wavData)
    {
        // WAV header: 44 bytes minimum, samples start after data chunk header
        if (wavData.Length < 44)
            throw new ArgumentException("Invalid WAV data: too short");

        // Find data chunk
        var pos = 12; // Skip RIFF header
        while (pos + 8 < wavData.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wavData, pos, 4);
            var chunkSize = BitConverter.ToInt32(wavData, pos + 4);

            if (chunkId == "data")
            {
                var dataStart = pos + 8;
                var sampleCount = chunkSize / 2; // 16-bit samples
                var samples = new float[sampleCount];
                for (var i = 0; i < sampleCount && dataStart + i * 2 + 1 < wavData.Length; i++)
                {
                    var sample = BitConverter.ToInt16(wavData, dataStart + i * 2);
                    samples[i] = sample / 32768f;
                }
                return samples;
            }

            pos += 8 + chunkSize;
            if (chunkSize % 2 != 0) pos++; // Padding byte
        }

        throw new ArgumentException("Invalid WAV data: no data chunk found");
    }

    /// <summary>
    /// Migrates model files from the old location (%LocalAppData%/TypeWhisper/Models/)
    /// to the plugin's data directory on first activation.
    /// </summary>
    private void MigrateModelFiles()
    {
        if (_host is null) return;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldModelsDir = Path.Combine(localAppData, "TypeWhisper", "Models");

        if (!Directory.Exists(oldModelsDir)) return;

        foreach (var model in _models)
        {
            var oldDir = Path.Combine(oldModelsDir, model.Id);
            if (!Directory.Exists(oldDir)) continue;

            var newDir = GetModelDirectory(model.Id);
            if (Directory.Exists(newDir) && model.Files.All(f => File.Exists(Path.Combine(newDir, f.FileName))))
                continue; // Already migrated

            Directory.CreateDirectory(newDir);

            foreach (var file in model.Files)
            {
                var oldPath = Path.Combine(oldDir, file.FileName);
                var newPath = Path.Combine(newDir, file.FileName);

                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    try
                    {
                        File.Move(oldPath, newPath);
                        Debug.WriteLine($"[SherpaOnnx] Migrated {file.FileName} for {model.Id}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SherpaOnnx] Failed to migrate {file.FileName}: {ex.Message}");
                    }
                }
            }

            // Clean up old directory if empty
            try
            {
                if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                    Directory.Delete(oldDir);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// A downloadable model and the files that make it up.
    /// </summary>
    internal sealed record ModelDefinition(
        string Id,
        string DisplayName,
        string Publisher,
        string SizeDescription,
        int EstimatedSizeMB,
        int LanguageCount,
        bool IsRecommended,
        bool SupportsTranslation,
        IReadOnlyList<ModelFileDefinition> Files,
        TranscribeCppAsset? Gguf = null);

    /// <summary>
    /// One model file, pinned to an immutable URL and the SHA-256 it must have. The hash is not
    /// optional so that no entry can be added without one.
    /// </summary>
    internal sealed record ModelFileDefinition(string FileName, string DownloadUrl, int EstimatedSizeMB, string Sha256);
}
