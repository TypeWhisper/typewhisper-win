using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CohereTranscribe;

internal sealed record CrispAsrServerConfiguration(
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
        if (!IsRunning || _baseUrl is null || _apiKey is null || _process is null)
            throw new InvalidOperationException("The local Cohere Transcribe model is not loaded.");

        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"The local CrispASR process exited unexpectedly with code {_process.ExitCode}.{GetOutputTail()}");
        }

        return await OpenAiTranscriptionHelper.TranscribeAsync(
            _httpClient,
            _baseUrl,
            _apiKey,
            CohereTranscribePlugin.ModelId,
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
            await process.WaitForExitAsync(timeout.Token);
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
        var startInfo = new ProcessStartInfo
        {
            FileName = configuration.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(configuration.ExecutablePath)
                ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        AddArgument(startInfo, "--server");
        AddArgument(startInfo, "--backend", "cohere");
        AddArgument(startInfo, "--model", configuration.ModelPaths.ModelPath);
        AddArgument(startInfo, "--host", "127.0.0.1");
        AddArgument(startInfo, "--port", port.ToString());
        AddArgument(startInfo, "--language", "auto");
        AddArgument(startInfo, "--lid-backend", "ecapa");
        AddArgument(startInfo, "--lid-model", configuration.ModelPaths.LanguageIdModelPath);
        AddArgument(startInfo, "--vad");
        AddArgument(startInfo, "--vad-model", configuration.ModelPaths.VadModelPath);
        AddArgument(startInfo, "--strict-pipeline");
        AddArgument(startInfo, "--require-vad");
        AddArgument(startInfo, "--threads", configuration.ThreadCount.ToString());
        AddArgument(startInfo, "--gpu-backend", GetBackendName(configuration.Backend));

        if (configuration.Backend == CrispAsrBackend.Cpu)
            AddArgument(startInfo, "--no-gpu");

        startInfo.Environment["CRISPASR_API_KEYS"] = apiKey;
        startInfo.Environment["CRISPASR_CACHE_DIR"] = configuration.ModelPaths.CacheDirectory;
        return startInfo;
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
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string GetBackendName(CrispAsrBackend backend) =>
        backend switch
        {
            CrispAsrBackend.Cpu => "cpu",
            CrispAsrBackend.Cuda => "cuda",
            CrispAsrBackend.Vulkan => "vulkan",
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };

    private static void AddArgument(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (var value in values)
            startInfo.ArgumentList.Add(value);
    }
}
