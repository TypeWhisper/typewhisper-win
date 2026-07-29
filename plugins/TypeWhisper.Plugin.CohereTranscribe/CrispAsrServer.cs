using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;
using Windows.Storage;

namespace TypeWhisper.Plugin.CohereTranscribe;

internal sealed record CrispAsrServerConfiguration(
    string ModelId,
    string ExecutablePath,
    CohereModelPaths ModelPaths,
    CrispAsrBackend Backend,
    int ThreadCount);

internal interface ICrispAsrServer : IDisposable
{
    bool IsRunning { get; }

    CrispAsrBackend? ActiveBackend { get; }

    Task StartAsync(CrispAsrServerConfiguration configuration, CancellationToken cancellationToken);

    Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        CancellationToken cancellationToken);

    Task StopAsync();
}

internal sealed class CrispAsrServer : ICrispAsrServer
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Action<PluginLogLevel, string> _log;
    private readonly object _outputLock = new();
    private readonly Queue<string> _outputTail = new();

    private Process? _process;
    private WindowsProcessJob? _processJob;
    private string? _baseUrl;
    private string? _apiKey;
    private string? _modelId;

    internal CrispAsrServer(Action<PluginLogLevel, string> log)
    {
        _log = log;
    }

    public bool IsRunning => _process is { HasExited: false } && _baseUrl is not null;

    public CrispAsrBackend? ActiveBackend { get; private set; }

    public async Task StartAsync(
        CrispAsrServerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await StopAsync();

        if (!File.Exists(configuration.ExecutablePath))
        {
            throw new FileNotFoundException(
                "The local CrispASR runtime executable is missing.",
                configuration.ExecutablePath);
        }

        Directory.CreateDirectory(configuration.ModelPaths.CacheDirectory);

        var port = ReserveLoopbackPort();
        var apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var startInfo = BuildStartInfo(configuration, port, apiKey);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) => CaptureOutput(args.Data);
        process.ErrorDataReceived += (_, args) => CaptureOutput(args.Data);

        lock (_outputLock)
            _outputTail.Clear();

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("CrispASR did not start.");

            _process = process;
            _processJob = WindowsProcessJob.CreateAndAssign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _baseUrl = $"http://127.0.0.1:{port}";
            _apiKey = apiKey;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            await WaitUntilReadyAsync(process, _baseUrl, timeout.Token);

            ActiveBackend = configuration.Backend;
            _modelId = configuration.ModelId;
            _log(
                PluginLogLevel.Info,
                $"CrispASR {CohereLocalAssetManager.CrispAsrVersion} is ready on loopback using {GetBackendName(configuration.Backend)}.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var output = GetOutputTail();
            await StopAsync();
            throw new TimeoutException(
                $"CrispASR did not become ready within {StartupTimeout.TotalMinutes:0} minutes.{output}",
                exception);
        }
        catch
        {
            if (_process is null)
                process.Dispose();
            await StopAsync();
            throw;
        }
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        CancellationToken cancellationToken)
    {
        if (!IsRunning
            || _baseUrl is null
            || _apiKey is null
            || _modelId is null
            || _process is null)
        {
            throw new InvalidOperationException("The local Cohere Transcribe model is not loaded.");
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"The local CrispASR process exited unexpectedly with code {_process.ExitCode}.{GetOutputTail()}");
        }

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            _baseUrl,
            _apiKey,
            _modelId,
            wavAudio,
            language,
            translate: false,
            responseFormat: "verbose_json",
            cancellationToken,
            prompt: null);
    }

    public async Task StopAsync()
    {
        var process = _process;
        var processJob = _processJob;
        _process = null;
        _processJob = null;
        _baseUrl = null;
        _apiKey = null;
        _modelId = null;
        ActiveBackend = null;

        if (process is null)
        {
            processJob?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log(PluginLogLevel.Warning, "Timed out while stopping the local CrispASR process.");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
            processJob?.Dispose();
        }
    }

    public void Dispose()
    {
        StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        _httpClient.Dispose();
    }

    internal static ProcessStartInfo BuildStartInfo(
        CrispAsrServerConfiguration configuration,
        int port,
        string apiKey)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var physicalLocalAppData = TryGetPhysicalLocalAppData();
        var executablePath = ResolveUnpackagedChildPath(
            configuration.ExecutablePath,
            localAppData,
            physicalLocalAppData);
        var modelPath = ResolveUnpackagedChildPath(
            configuration.ModelPaths.ModelPath,
            localAppData,
            physicalLocalAppData);
        var languageIdModelPath = ResolveUnpackagedChildPath(
            configuration.ModelPaths.LanguageIdModelPath,
            localAppData,
            physicalLocalAppData);
        var vadModelPath = ResolveUnpackagedChildPath(
            configuration.ModelPaths.VadModelPath,
            localAppData,
            physicalLocalAppData);
        var cacheDirectory = ResolveUnpackagedChildPath(
            configuration.ModelPaths.CacheDirectory,
            localAppData,
            physicalLocalAppData);
        var runtimeDirectory = Path.GetDirectoryName(executablePath)
            ?? AppContext.BaseDirectory;
        var backend = GetBackendName(configuration.Backend);
        // cmd.exe /s strips the outermost quote pair. The doubled leading quote
        // preserves the executable's quoted path, including paths with spaces.
        var command =
            "\"\"%TYPEWHISPER_CRISPASR_EXECUTABLE%\""
            + " --server"
            + " --backend cohere"
            + " --model \"%TYPEWHISPER_CRISPASR_MODEL%\""
            + " --host 127.0.0.1"
            + $" --port {port.ToString(CultureInfo.InvariantCulture)}"
            + " --language auto"
            + " --lid-backend ecapa"
            + " --lid-model \"%TYPEWHISPER_CRISPASR_LID_MODEL%\""
            + " --vad"
            + " --vad-model \"%TYPEWHISPER_CRISPASR_VAD_MODEL%\""
            + " --strict-pipeline"
            + " --require-vad"
            + $" --threads {configuration.ThreadCount.ToString(CultureInfo.InvariantCulture)}"
            + $" --gpu-backend {backend}"
            + (configuration.Backend == CrispAsrBackend.Cpu ? " --no-gpu" : string.Empty)
            + "\"";

        var startInfo = new ProcessStartInfo
        {
            // An executable launched directly by an MSIX desktop process inherits
            // its packaged DLL search order. The intermediate command process makes
            // CrispASR a grandchild, which Windows starts outside that environment.
            FileName = Path.Join(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /v:off /c {command}",
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.Environment["CRISPASR_API_KEYS"] = apiKey;
        startInfo.Environment["CRISPASR_CACHE_DIR"] = cacheDirectory;
        startInfo.Environment["TYPEWHISPER_CRISPASR_EXECUTABLE"] = executablePath;
        startInfo.Environment["TYPEWHISPER_CRISPASR_MODEL"] = modelPath;
        startInfo.Environment["TYPEWHISPER_CRISPASR_LID_MODEL"] = languageIdModelPath;
        startInfo.Environment["TYPEWHISPER_CRISPASR_VAD_MODEL"] = vadModelPath;
        return startInfo;
    }

    internal static string ResolveUnpackagedChildPath(
        string path,
        string localAppData,
        string? physicalLocalAppData)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(localAppData) || string.IsNullOrWhiteSpace(physicalLocalAppData))
            return resolvedPath;

        var localAppDataRoot = Path.GetFullPath(localAppData);
        var physicalLocalAppDataRoot = Path.GetFullPath(physicalLocalAppData);

        if (IsSameOrUnderDirectory(resolvedPath, physicalLocalAppDataRoot)
            || !IsSameOrUnderDirectory(resolvedPath, localAppDataRoot))
        {
            return resolvedPath;
        }

        return Path.Join(
            physicalLocalAppDataRoot,
            Path.GetRelativePath(localAppDataRoot, resolvedPath));
    }

    private static bool IsSameOrUnderDirectory(string path, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return path.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(
                normalizedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetPhysicalLocalAppData()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return null;

        try
        {
            var localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
            return string.IsNullOrWhiteSpace(localCachePath)
                ? null
                : Path.Join(localCachePath, "Local");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private async Task WaitUntilReadyAsync(
        Process process,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"CrispASR exited during startup with code {process.ExitCode}.{GetOutputTail()}");
            }

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await _httpClient.GetAsync(
                    $"{baseUrl}/health",
                    attempt.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (Exception exception) when (
                (exception is HttpRequestException
                    or TaskCanceledException
                    or OperationCanceledException)
                && !cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    private void CaptureOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_outputLock)
        {
            _outputTail.Enqueue(line.Trim());
            while (_outputTail.Count > 80)
                _outputTail.Dequeue();
        }
    }

    private string GetOutputTail()
    {
        lock (_outputLock)
        {
            if (_outputTail.Count == 0)
                return string.Empty;

            return Environment.NewLine + string.Join(
                Environment.NewLine,
                _outputTail.TakeLast(20));
        }
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetBackendName(CrispAsrBackend backend) =>
        backend switch
        {
            CrispAsrBackend.Cpu => "cpu",
            CrispAsrBackend.Cuda => "cuda",
            CrispAsrBackend.Vulkan => "vulkan",
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };

}
