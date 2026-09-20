using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GraniteSpeech;

/// <summary>
/// Provides granite speech plugin behavior.
/// </summary>
public sealed partial class GraniteSpeechPlugin : ITypeWhisperPlugin, IPcmTranscriptionEnginePlugin, IPluginTextSettings, IPluginSettingsActions
{
    private const string ModelId = "granite-4.0-1b-speech";
    private static readonly string TorchRequirement = ReadRequirement("torch");
    private static readonly string TorchAudioRequirement = ReadRequirement("torchaudio");
    internal static string RuntimeRevision => TorchRequirement.Replace("==", "-", StringComparison.Ordinal) + "-v1";
    internal static string RuntimeWheels(string device)
    {
        var flavor = device == "Cpu" ? "+cpu" : "+cu130";
        return TorchRequirement + flavor + " " + TorchAudioRequirement + flavor;
    }
    private static string ReadRequirement(string package) => File.ReadLines(GetScriptPath("requirements.txt"))
        .Select(line => line.Trim()).Single(line => line.StartsWith(package + "==", StringComparison.Ordinal));
    private const string PythonVersion = "3.12.10";
    private const string PythonEmbedUrl =
        $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip";
    private const string PythonEmbedSha256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3";
    private const string GetPipUrl = "https://raw.githubusercontent.com/pypa/get-pip/af54dfe793b24685f8dc4ebba0630d9f2d77653c/public/get-pip.py";
    private const string GetPipSha256 = "fb24e693bab954209a063d90953621412ccad4a500905a726286e038f508ddf6";

    private static readonly IReadOnlyList<string> GraniteSupportedLanguages =
        ["en", "fr", "de", "es", "pt", "ja"];

    private readonly SemaphoreSlim _sidecarLock = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private IPluginHostServices? _host;
    private Process? _sidecar;
    private StreamWriter? _sidecarIn;
    private StreamReader? _sidecarOut;
    private string? _selectedModelId;
    private string? _loadedModelId;
    private int _requestId;

    // ITypeWhisperPlugin
    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.granite-speech";
    /// <summary>
    /// Performs speech.
    /// </summary>
    public string PluginName => "IBM Granite Speech (Local)";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.2.6";

    // ITranscriptionEnginePlugin
    /// <summary>
    /// Gets the stable provider identifier used for model and settings selection.
    /// </summary>
    public string ProviderId => "granite-speech";
    /// <summary>
    /// Gets the provider display name.
    /// </summary>
    public string ProviderDisplayName => L("Local (Granite Speech)", "Lokal (Granite Speech)");
    /// <summary>
    /// Gets whether the provider has the configuration required to run.
    /// </summary>
    /// <inheritdoc />
    public bool IsConfigured => _host is not null && IsModelDownloaded(ModelId);
    /// <inheritdoc />
    public bool SupportsLocalLivePreview => true;
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
    /// Gets whether the local Python and model assets can be removed.
    /// </summary>
    public bool SupportsModelRemoval => true;
    /// <summary>
    /// Gets the language codes accepted by the provider.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => GraniteSupportedLanguages;

    /// <summary>
    /// Gets the transcription models exposed by this provider.
    /// </summary>
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [
        new(ModelId, "IBM Granite 4.0 1B Speech")
        {
            SizeDescription = "~5–8 GB (runtime + model)",
            EstimatedSizeMB = 8000,
            IsRecommended = true,
            LanguageCount = GraniteSupportedLanguages.Count,
            LanguageCodes = GraniteSupportedLanguages,
        }
    ];

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _selectedModelId = ModelId;
        _device = host.GetSetting<string>("device") is "Cpu" ? "Cpu" : host.GetSetting<string>("device") is "NvidiaCuda" ? "NvidiaCuda" : "Auto";
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync() => UnloadModelAsync();



    /// <summary>
    /// Selects the provider model used for subsequent requests.
    /// </summary>
    public void SelectModel(string modelId)
    {
        if (modelId != ModelId)
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    /// <summary>
    /// Gets whether the requested model is available locally.
    /// </summary>
    public bool IsModelDownloaded(string modelId)
    {
        if (modelId != ModelId || _host is null) return false;
        var marker = Path.Combine(GetDataDirectory(), ".setup-complete");
        if (!File.Exists(marker)) return false;
        var runtime = File.ReadAllText(marker);
        return runtime == RuntimeRevision + "|cuda" || _device == "Cpu" && runtime == RuntimeRevision + "|cpu";
    }

    /// <summary>
    /// Downloads the requested model and reports progress when available.
    /// </summary>
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        if (modelId != ModelId) throw new ArgumentException("Unknown model.", nameof(modelId));
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) throw new PlatformNotSupportedException("Granite Speech requires Windows x64.");
        await _sidecarLock.WaitAsync(ct);
        try {
        var dataDir = GetDataDirectory();
        Directory.CreateDirectory(dataDir);

        var pythonDir = Path.Combine(dataDir, "python");
        var pythonExe = Path.Combine(pythonDir, "python.exe");

        // Step 1: Download & extract embedded Python (~11 MB)
        if (!File.Exists(pythonExe) || !ValidatePythonInstallation(pythonDir))
        {
            progress?.Report(0.0);
            Log(PluginLogLevel.Info, "Step 1: Downloading embedded Python...");

            if (Directory.Exists(pythonDir))
            {
                Log(PluginLogLevel.Warning, "Incomplete Python installation detected, cleaning up...");
                Directory.Delete(pythonDir, recursive: true);
            }

            await RetryAsync(async () =>
            {
                if (Directory.Exists(pythonDir))
                    Directory.Delete(pythonDir, recursive: true);

                var zipPath = Path.Combine(dataDir, "python-embed.zip");
                await DownloadFileAsync(_httpClient, PythonEmbedUrl, zipPath, PythonEmbedSha256, ct);
                Directory.CreateDirectory(pythonDir);
                ZipFile.ExtractToDirectory(zipPath, pythonDir, overwriteFiles: true);
                File.Delete(zipPath);
                PatchPthFile(pythonDir);

                if (!File.Exists(pythonExe))
                    throw new InvalidOperationException("python.exe not found after extraction");
                if (!ValidatePythonInstallation(pythonDir))
                    throw new InvalidOperationException("Python installation validation failed after extraction");
            }, maxRetries: 2, ct);

            Log(PluginLogLevel.Info, "Step 1 complete: Python installed");
        }

        pythonDir = ExtendedPythonPath(pythonDir);
        pythonExe = Path.Combine(pythonDir, "python.exe");

        // Step 2: Bootstrap pip
        progress?.Report(0.05);
        if (!File.Exists(Path.Combine(pythonDir, "Scripts", "pip.exe")))
        {
            Log(PluginLogLevel.Info, "Step 2: Bootstrapping pip...");

            await RetryAsync(async () =>
            {
                var getPipPath = Path.Combine(pythonDir, "get-pip.py");
                await DownloadFileAsync(_httpClient, GetPipUrl, getPipPath, GetPipSha256, ct);
                await RunProcessAsync(pythonExe, $"\"{getPipPath}\"", ct, timeoutMs: 300_000);
                File.Delete(getPipPath);

                if (!File.Exists(Path.Combine(pythonDir, "Scripts", "pip.exe")))
                    throw new InvalidOperationException("pip.exe not found after bootstrap");
            }, maxRetries: 2, ct);

            Log(PluginLogLevel.Info, "Step 2 complete: pip installed");
        }

        // Step 3: Install packages (torch CPU ~300 MB, transformers, soundfile)
        File.Delete(Path.Combine(dataDir, ".setup-complete"));
        progress?.Report(0.10);
        Log(PluginLogLevel.Info, "Step 3: Installing Python packages (this may take a while)...");

        var reqPath = GetScriptPath("requirements.txt");
        await RetryAsync(async () =>
        {
            await RunProcessAsync(pythonExe,
                $"-m pip install -q --no-cache-dir -r \"{reqPath}\" " +
                RuntimeWheels(_device) + " " +
                (_device == "Cpu" ? "--index-url https://download.pytorch.org/whl/cpu " : "--index-url https://download.pytorch.org/whl/cu130 ") +
                "--extra-index-url https://pypi.org/simple/",
                ct, timeoutMs: 1_800_000);

            await RunProcessAsync(pythonExe,
                "-c \"import torch; import transformers; import soundfile; import huggingface_hub\"",
                ct, timeoutMs: 120_000);
        }, maxRetries: 2, ct);

        Log(PluginLogLevel.Info, "Step 3 complete: packages installed");

        // Step 4: Download HF model (~4.5 GB, with per-file progress)
        progress?.Report(0.30);
        Log(PluginLogLevel.Info, "Step 4: Downloading model files...");

        var scriptPath = GetScriptPath("granite_speech_server.py");
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            ArgumentList = { scriptPath, "--setup" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["HF_HOME"] = Path.Join(dataDir, "hf-cache");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start model download");

        using var setupCancellation = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var setupErrors = proc.StandardError.ReadToEndAsync(ct);
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("progress", out var prog))
                {
                    // Scale Python's 0–1 into our 0.30–1.0 range
                    progress?.Report(0.30 + prog.GetDouble() * 0.70);
                }

                if (root.TryGetProperty("warning", out var warn))
                    Log(PluginLogLevel.Warning, $"Model download: {warn.GetString()}");

                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException($"Model download failed: {err.GetString()}");
            }
            catch (JsonException)
            {
                Debug.WriteLine($"[GraniteSpeech] Setup: {line}");
            }
        }

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var stderr = await setupErrors;
            throw new InvalidOperationException(
                $"Model download failed (exit {proc.ExitCode}): {stderr[Math.Max(0, stderr.Length - 1000)..]}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(dataDir, ".setup-complete"),
            RuntimeRevision + (_device == "Cpu" ? "|cpu" : "|cuda"), ct);

        Log(PluginLogLevel.Info, "Setup complete");
        progress?.Report(1.0);
        } finally { _sidecarLock.Release(); }
    }

    /// <summary>
    /// Stops the sidecar and removes all downloaded Granite Speech assets.
    /// </summary>
    public async Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        if (!string.Equals(modelId, ModelId, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown model: {modelId}", nameof(modelId));

        var host = _host ?? throw new InvalidOperationException("Plugin is not activated.");

        await _sidecarLock.WaitAsync(ct);
        try
        {
            StopSidecar();
            _loadedModelId = null;
            ct.ThrowIfCancellationRequested();

            var dataDirectory = GetDataDirectory();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
            _selectedModelId = null;
            host.NotifyCapabilitiesChanged();
        }
        finally
        {
            _sidecarLock.Release();
        }
    }

    /// <summary>
    /// Loads the selected transcription model into memory.
    /// </summary>
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        if (modelId != ModelId) throw new ArgumentException("Unknown model.", nameof(modelId));
        if (!IsModelDownloaded(modelId))
            throw new FileNotFoundException("Model not set up. Run DownloadModelAsync first.");

        await _sidecarLock.WaitAsync(ct);
        try
        {
            if (_loadedModelId == modelId && _sidecar is { HasExited: false }) return;
            StopSidecar();
            StartSidecar();

            var response = await SendCommandAsync(new { cmd = "load" }, ct);
            if (response.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"Failed to load model: {err.GetString()}");

            _activeDevice = response.TryGetProperty("device", out var device) ? device.GetString() : "cpu";
            _loadedModelId = modelId;
            _selectedModelId = modelId;
            Debug.WriteLine("[GraniteSpeech] Model loaded via Python sidecar");
        }
        finally
        {
            _sidecarLock.Release();
        }
    }

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (language is not null && !SupportedLanguages.Contains(language)) throw new ArgumentException("Unsupported spoken language.", nameof(language));
        if (_loadedModelId is null || _sidecar is null || _sidecar.HasExited) await LoadModelAsync(ModelId, ct);
        await _sidecarLock.WaitAsync(ct);
        try
        {
            if (_sidecar is null || _sidecar.HasExited)
                throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

            var response = await SendCommandAsync(new
            {
                cmd = "transcribe",
                audio_base64 = Convert.ToBase64String(wavAudio),
                language,
                translate,
            }, ct);

            if (response.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"Transcription failed: {err.GetString()}");

            var text = response.GetProperty("text").GetString() ?? "";
            var duration = response.GetProperty("duration").GetDouble();

            return new PluginTranscriptionResult(text, language, duration, NoSpeechProbability: null);
        }
        finally
        {
            _sidecarLock.Release();
        }
    }

    /// <summary>
    /// Unloads model asynchronously..
    /// </summary>
    public async Task UnloadModelAsync()
    {
        await _sidecarLock.WaitAsync();
        try
        {
            StopSidecar();
            _loadedModelId = null;
        }
        finally
        {
            _sidecarLock.Release();
        }
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        StopSidecar();
        _sidecarLock.Dispose();
        _httpClient.Dispose();
    }

    // --- Sidecar management ---

    private void StartSidecar()
    {
        var pythonExe = Path.Combine(ExtendedPythonPath(Path.Combine(GetDataDirectory(), "python")), "python.exe");
        var scriptPath = GetScriptPath("granite_speech_server.py");

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            ArgumentList = { scriptPath, "--serve" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["TYPEWHISPER_DEVICE"] = _device;
        psi.Environment["HF_HUB_OFFLINE"] = "1";
        psi.Environment["HF_HOME"] = Path.Join(GetDataDirectory(), "hf-cache");

        _sidecar = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Python sidecar");
        _sidecarIn = _sidecar.StandardInput;
        _sidecarOut = _sidecar.StandardOutput;
        _ = DrainErrorsAsync(_sidecar.StandardError);
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    { try { while (await reader.ReadLineAsync() is not null) { } } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }

    private void StopSidecar(bool terminate = false)
    {
        if (_sidecar is null) return;

        try
        {
            if (!terminate && !_sidecar.HasExited)
            {
                _sidecarIn?.WriteLine(JsonSerializer.Serialize(new { cmd = "quit" }));
                _sidecarIn?.Flush();
                _sidecar.WaitForExit(3000);
            }
        }
        catch { /* ignore */ }

        if (!_sidecar.HasExited)
        {
            try { _sidecar.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (_sidecar.HasExited) { }
            // Keep ownership and block replacement if termination did not drain.
            // A later stop can retry; never overlap two GPU sidecars.
            if (!_sidecar.WaitForExit(10000))
                throw new TimeoutException("The local speech process did not stop. Try unloading the model again.");
        }

        _sidecar.Dispose();
        _sidecar = null;
        _sidecarIn = null;
        _sidecarOut = null;
        _loadedModelId = null;
        _activeDevice = null;
    }

    private async Task<JsonElement> SendCommandAsync(object command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_sidecarIn is null || _sidecarOut is null)
            throw new InvalidOperationException("Sidecar not running");
        try
        {
        var reqId = Interlocked.Increment(ref _requestId);

        // Wrap command with request ID
        var wrapper = new Dictionary<string, object?>();
        foreach (var prop in JsonSerializer.SerializeToElement(command).EnumerateObject())
            wrapper[prop.Name] = prop.Value;
        wrapper["req_id"] = reqId;

        var json = JsonSerializer.Serialize(wrapper);
        await _sidecarIn.WriteLineAsync(json.AsMemory(), ct);
        await _sidecarIn.FlushAsync(ct);

        // Read responses until we find one matching our request ID
        // (drains any orphaned responses from cancelled requests)
        while (true)
        {
            var response = await _sidecarOut.ReadLineAsync(ct)
                ?? throw new InvalidOperationException("Sidecar process closed unexpectedly");

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement.Clone();

            if (root.TryGetProperty("req_id", out var id) && id.GetInt32() == reqId)
                return root;

            Debug.WriteLine($"[GraniteSpeech] Skipping stale response (req_id={id})");
        }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StopSidecar(terminate: true);
            throw;
        }
    }

    // --- Setup helpers ---

    internal static async Task DownloadFileAsync(HttpClient httpClient, string url, string destPath, string expectedSha256, CancellationToken ct)
    {
        var temporaryPath = destPath + ".tmp";
        try
        {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(temporaryPath, FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, true);
        await contentStream.CopyToAsync(fileStream, ct);
        fileStream.Close();

        await using (var verification = File.OpenRead(temporaryPath))
        {
            var actualHash = await SHA256.HashDataAsync(verification, ct);
            if (!Convert.ToHexString(actualHash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime download failed SHA-256 verification.");
        }
        File.Move(temporaryPath, destPath, overwrite: true);
        }
        finally { File.Delete(temporaryPath); }
    }

    internal static string ExtendedPythonPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    private static void PatchPthFile(string pythonDir)
    {
        // The ._pth file restricts sys.path in embeddable Python.
        // We must uncomment "import site" so pip-installed packages are importable.
        var pthFiles = Directory.GetFiles(pythonDir, "python*._pth");
        foreach (var pthFile in pthFiles)
        {
            var content = File.ReadAllText(pthFile);
            content = content.Replace("#import site", "import site");
            File.WriteAllText(pthFile, content);
        }
    }

    private static bool ValidatePythonInstallation(string pythonDir)
    {
        if (!File.Exists(Path.Combine(pythonDir, "python.exe")))
            return false;
        if (Directory.GetFiles(pythonDir, "python*.zip").Length == 0)
            return false;
        if (Directory.GetFiles(pythonDir, "python*._pth").Length == 0)
            return false;
        return true;
    }

    // --- General helpers ---

    private void Log(PluginLogLevel level, string message)
    {
        _host?.Log(level, message);
        Debug.WriteLine($"[GraniteSpeech] {message}");
    }

    private string GetDataDirectory() =>
        Path.Combine((_host ?? throw new InvalidOperationException("Plugin is not activated.")).PluginAssetDirectory, "managed-runtime");

    private static string GetScriptPath(string fileName)
    {
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        return Path.Combine(pluginDir, "Scripts", fileName);
    }

    private static async Task RunProcessAsync(string exe, string args, CancellationToken ct,
        int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exe}");

        var errors = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            await proc.WaitForExitAsync();
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"Process timed out after {timeoutMs / 1000}s: {exe}");
        }
        await output;

        if (proc.ExitCode != 0)
        {
            var stderr = await errors;
            throw new InvalidOperationException(
                $"{exe} failed (exit {proc.ExitCode}): {stderr[Math.Max(0, stderr.Length - 500)..]}");
        }
    }

    private async Task RetryAsync(Func<Task> action, int maxRetries, CancellationToken ct,
        Action<int, Exception>? onRetry = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Log(PluginLogLevel.Warning, $"Attempt {attempt + 1} failed ({ex.Message}), retrying in {delay.TotalSeconds}s...");
                onRetry?.Invoke(attempt + 1, ex);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException
        or TimeoutException
        or IOException
        || (ex is InvalidOperationException ioe && ioe.Message.Contains("failed (exit"));
}
