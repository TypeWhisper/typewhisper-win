using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GraniteSpeech;

public sealed class GraniteSpeechPlugin : ITypeWhisperPlugin, ITranscriptionEnginePlugin
{
    private const string ModelId = "granite-4.0-1b-speech";
    private const string PythonVersion = "3.12.10";
    private const string PythonEmbedUrl =
        $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip";
    private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";

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
    public string PluginId => "com.typewhisper.granite-speech";
    public string PluginName => "IBM Granite Speech (Local)";
    public string PluginVersion => "1.0.1";

    // ITranscriptionEnginePlugin
    public string ProviderId => "granite-speech";
    public string ProviderDisplayName => "Lokal (Granite Speech)";
    public bool IsConfigured => true;
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => true;
    public bool SupportsModelDownload => true;
    public IReadOnlyList<string> SupportedLanguages => GraniteSupportedLanguages;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [
        new(ModelId, "IBM Granite 4.0 1B Speech")
        {
            SizeDescription = "~5 GB (Python + model)",
            EstimatedSizeMB = 5000,
            IsRecommended = false,
            LanguageCount = 6,
        }
    ];

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        StopSidecar();
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => null;

    public void SelectModel(string modelId)
    {
        if (modelId != ModelId)
            throw new ArgumentException($"Unknown model: {modelId}");
        _selectedModelId = modelId;
    }

    public bool IsModelDownloaded(string modelId) =>
        File.Exists(Path.Combine(GetDataDirectory(), ".setup-complete"));

    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
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
                await DownloadFileAsync(PythonEmbedUrl, zipPath, ct);
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

        // Step 2: Bootstrap pip
        progress?.Report(0.05);
        if (!File.Exists(Path.Combine(pythonDir, "Scripts", "pip.exe")))
        {
            Log(PluginLogLevel.Info, "Step 2: Bootstrapping pip...");

            await RetryAsync(async () =>
            {
                var getPipPath = Path.Combine(pythonDir, "get-pip.py");
                await DownloadFileAsync(GetPipUrl, getPipPath, ct);
                await RunProcessAsync(pythonExe, $"\"{getPipPath}\"", ct, timeoutMs: 300_000);
                File.Delete(getPipPath);

                if (!File.Exists(Path.Combine(pythonDir, "Scripts", "pip.exe")))
                    throw new InvalidOperationException("pip.exe not found after bootstrap");
            }, maxRetries: 2, ct);

            Log(PluginLogLevel.Info, "Step 2 complete: pip installed");
        }

        // Step 3: Install packages (torch CPU ~300 MB, transformers, soundfile)
        progress?.Report(0.10);
        Log(PluginLogLevel.Info, "Step 3: Installing Python packages (this may take a while)...");

        var reqPath = GetScriptPath("requirements.txt");
        await RetryAsync(async () =>
        {
            await RunProcessAsync(pythonExe,
                $"-m pip install -q --no-cache-dir -r \"{reqPath}\" " +
                "--index-url https://download.pytorch.org/whl/cpu " +
                "--extra-index-url https://pypi.org/simple/",
                ct, timeoutMs: 1_800_000);

            await RunProcessAsync(pythonExe,
                "-c \"import torch; import transformers; import soundfile; import huggingface_hub\"",
                ct, timeoutMs: 30_000);
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

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start model download");

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
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"Model download failed (exit {proc.ExitCode}): {stderr[Math.Max(0, stderr.Length - 1000)..]}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(dataDir, ".setup-complete"),
            DateTime.UtcNow.ToString("O"), ct);

        Log(PluginLogLevel.Info, "Setup complete");
        progress?.Report(1.0);
    }

    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        if (!IsModelDownloaded(modelId))
            throw new FileNotFoundException("Model not set up. Run DownloadModelAsync first.");

        await _sidecarLock.WaitAsync(ct);
        try
        {
            StopSidecar();
            StartSidecar();

            var response = await SendCommandAsync(new { cmd = "load" }, ct);
            if (response.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"Failed to load model: {err.GetString()}");

            _loadedModelId = modelId;
            _selectedModelId = modelId;
            Debug.WriteLine("[GraniteSpeech] Model loaded via Python sidecar");
        }
        finally
        {
            _sidecarLock.Release();
        }
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
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

    public void Dispose()
    {
        StopSidecar();
        _sidecarLock.Dispose();
        _httpClient.Dispose();
    }

    // --- Sidecar management ---

    private void StartSidecar()
    {
        var pythonExe = Path.Combine(GetDataDirectory(), "python", "python.exe");
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

        _sidecar = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Python sidecar");
        _sidecarIn = _sidecar.StandardInput;
        _sidecarOut = _sidecar.StandardOutput;
    }

    private void StopSidecar()
    {
        if (_sidecar is null) return;

        try
        {
            if (!_sidecar.HasExited)
            {
                _sidecarIn?.WriteLine(JsonSerializer.Serialize(new { cmd = "quit" }));
                _sidecarIn?.Flush();
                _sidecar.WaitForExit(3000);
            }
        }
        catch { /* ignore */ }

        if (!_sidecar.HasExited)
        {
            try { _sidecar.Kill(); }
            catch { /* ignore */ }
        }

        _sidecar.Dispose();
        _sidecar = null;
        _sidecarIn = null;
        _sidecarOut = null;
        _loadedModelId = null;
    }

    private async Task<JsonElement> SendCommandAsync(object command, CancellationToken ct)
    {
        if (_sidecarIn is null || _sidecarOut is null)
            throw new InvalidOperationException("Sidecar not running");

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

    // --- Setup helpers ---

    private async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath + ".tmp", FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, true);
        await contentStream.CopyToAsync(fileStream, ct);
        fileStream.Close();

        File.Move(destPath + ".tmp", destPath, overwrite: true);
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
        _host?.PluginDataDirectory ?? Path.Combine(".", "PluginData");

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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            proc.Kill();
            throw new TimeoutException($"Process timed out after {timeoutMs / 1000}s: {exe}");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
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
