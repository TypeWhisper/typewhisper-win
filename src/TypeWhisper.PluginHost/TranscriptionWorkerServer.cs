using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>
/// Runs one local transcription engine in a separate process. The host starts its own executable with
/// <see cref="Argument"/> first, so a native crash in the engine ends only this process.
/// </summary>
public static class TranscriptionWorkerServer
{
    /// <summary>The first command-line argument that selects worker mode.</summary>
    public const string Argument = "--transcription-worker";
    internal const int ExitInvalidArguments = 2;
    internal const int ExitParentGone = 3;
    internal const int ExitEngineUnavailable = 4;
    internal const int ExitConnectionFailed = 5;

    /// <summary>Whether the process was started as a transcription worker.</summary>
    public static bool IsWorkerInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Argument;

    /// <summary>Serves requests until the host disconnects, asks it to stop or exits; returns the process exit code.</summary>
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        SuppressCrashDialogs();
        if (!TranscriptionWorkerArguments.TryParse(args, out var options)) return ExitInvalidArguments;
        if (!WatchParent(options.ParentProcessId)) return ExitParentGone;
        await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return ExitConnectionFailed; }
        var connection = new Connection(pipe);
        var services = new TranscriptionWorkerHostServices(options.DataDirectory, options.AssetDirectory,
            (level, message) => connection.Post(new() { Type = TranscriptionWorkerMessageTypes.Log, LogLevel = level, LogMessage = message }));
        IPcmTranscriptionEnginePlugin engine;
        try
        {
            // The package stays loaded until the process ends. Exiting releases native memory
            // more reliably than disposing a context that native code may still reference.
            var package = await PortablePluginPackage.LoadAsync(options.PackageDirectory, services, options.HostVersion).ConfigureAwait(false);
            engine = FindEngine(package.Plugin, options.SelectionId)
                ?? throw new NotSupportedException("The plugin does not provide this local transcription engine.");
            if (engine.AccelerationPreference != options.Acceleration) engine.SetAccelerationPreference(options.Acceleration);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await connection.SendAsync(new()
            {
                Type = TranscriptionWorkerMessageTypes.Hello, ProtocolVersion = TranscriptionWorkerProtocol.Version,
                ProcessId = Environment.ProcessId, Error = TranscriptionWorkerProtocol.ToError(ex)
            }).ConfigureAwait(false);
            return ExitEngineUnavailable;
        }
        var session = new Session(engine, connection, options.HeartbeatInterval);
        // Announcing the beat lets the host arm its inactivity watchdog only for a worker that will beat.
        await connection.SendAsync(new()
        {
            Type = TranscriptionWorkerMessageTypes.Hello, ProtocolVersion = TranscriptionWorkerProtocol.Version,
            ProcessId = Environment.ProcessId, State = session.State(), HeartbeatInterval = options.HeartbeatInterval
        }).ConfigureAwait(false);
        return await session.RunAsync().ConfigureAwait(false);
    }

    private static IPcmTranscriptionEnginePlugin? FindEngine(ITypeWhisperPlugin plugin, string selectionId) =>
        (plugin is ITranscriptionEnginePlugin direct ? new[] { direct } : [])
            .Concat(plugin is IAdditionalTranscriptionEnginesProvider additional ? additional.AdditionalTranscriptionEngines : [])
            .OfType<IPcmTranscriptionEnginePlugin>()
            .FirstOrDefault(engine => string.Equals(engine.GetTranscriptionSelectionId(), selectionId, StringComparison.OrdinalIgnoreCase));

    // The host owns the worker's lifetime. If it disappears without closing the pipe, stop at once.
    private static bool WatchParent(int parentProcessId)
    {
        try
        {
            var parent = Process.GetProcessById(parentProcessId);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => Environment.Exit(ExitParentGone);
            return !parent.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    // A native crash must end the process at once. Windows Error Reporting would otherwise keep the
    // crashed worker alive behind a "stopped working" dialog, and the host would wait for it.
    internal static void SuppressCrashDialogs()
    {
        if (!OperatingSystem.IsWindows()) return;
        const uint FailCriticalErrors = 0x0001, NoGpFaultErrorBox = 0x0002, NoOpenFileErrorBox = 0x8000;
        SetErrorMode(FailCriticalErrors | NoGpFaultErrorBox | NoOpenFileErrorBox);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private sealed class Connection(Stream stream)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        internal Stream Stream => stream;

        internal async Task SendAsync(TranscriptionWorkerMessage message, ReadOnlyMemory<byte> payload = default)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try { await TranscriptionWorkerProtocol.WriteAsync(stream, message, payload, CancellationToken.None).ConfigureAwait(false); }
            finally { _write.Release(); }
        }

        // Logging must not block the plugin thread that produced it, nor fail its operation.
        internal void Post(TranscriptionWorkerMessage message) => _ = Task.Run(async () =>
        {
            try { await SendAsync(message).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
        });
    }

    private sealed class Session(IPcmTranscriptionEnginePlugin engine, Connection connection, TimeSpan? heartbeatInterval)
    {
        private readonly Channel<(TranscriptionWorkerMessage Message, byte[] Payload)> _requests =
            Channel.CreateUnbounded<(TranscriptionWorkerMessage, byte[])>(new() { SingleReader = true, SingleWriter = true });
        private readonly object _sync = new();
        private readonly HashSet<long> _canceledBeforeStart = [];
        private long _current;
        private long _lastStarted;
        private CancellationTokenSource? _currentCancellation;
        private string? _loadedModelId;

        internal TranscriptionWorkerState State() => new(engine.SelectedModelId, _loadedModelId, engine.AccelerationPreference, engine.AccelerationStatus);

        internal async Task<int> RunAsync()
        {
            var reader = ReadAsync();
            await foreach (var (message, payload) in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (message.Command == TranscriptionWorkerCommands.Shutdown)
                {
                    await connection.SendAsync(Response(message.Id, error: null, result: null)).ConfigureAwait(false);
                    return 0;
                }
                using var cancellation = new CancellationTokenSource();
                lock (_sync)
                {
                    _current = _lastStarted = message.Id;
                    _currentCancellation = cancellation;
                    if (_canceledBeforeStart.Remove(message.Id)) cancellation.Cancel();
                }
                TranscriptionWorkerResult? result = null;
                TranscriptionWorkerError? error = null;
                using var beating = new CancellationTokenSource();
                var heartbeat = HeartbeatAsync(message.Id, beating.Token);
                try { result = await ExecuteAsync(message, payload, cancellation.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { error = TranscriptionWorkerProtocol.ToError(ex); }
                finally { lock (_sync) { _current = 0; _currentCancellation = null; } beating.Cancel(); }
                // The response is the last frame for this id.
                await heartbeat.ConfigureAwait(false);
                try { await connection.SendAsync(Response(message.Id, error, result)).ConfigureAwait(false); }
                catch (InvalidDataException ex) { await connection.SendAsync(Response(message.Id, TranscriptionWorkerProtocol.ToError(ex), null)).ConfigureAwait(false); }
            }
            await reader.ConfigureAwait(false);
            return 0;
        }

        private TranscriptionWorkerMessage Response(long id, TranscriptionWorkerError? error, TranscriptionWorkerResult? result) => new()
        {
            Type = TranscriptionWorkerMessageTypes.Response, Id = id, Error = error, Result = result, State = State()
        };

        // A hung native call keeps this process alive, so the host cannot tell a long decode from a worker that
        // no longer does anything. The beat says that the runtime still schedules work; it runs on the thread pool
        // next to the engine call, so a stall inside that call is not visible here, only a frozen worker is.
        private async Task HeartbeatAsync(long id, CancellationToken ct)
        {
            if (heartbeatInterval is not { } interval) return;
            try
            {
                while (true)
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    await connection.SendAsync(new() { Type = TranscriptionWorkerMessageTypes.Heartbeat, Id = id }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
        }

        // Reads on its own so a cancellation can reach a request that is still decoding.
        private async Task ReadAsync()
        {
            try
            {
                while (await TranscriptionWorkerProtocol.ReadAsync(connection.Stream, CancellationToken.None).ConfigureAwait(false) is { } frame)
                {
                    if (frame.Message.Type == TranscriptionWorkerMessageTypes.Cancel)
                    {
                        lock (_sync)
                        {
                            if (_current == frame.Message.Id) _currentCancellation?.Cancel();
                            // Request ids only grow, so a late cancellation of a finished request is ignored.
                            else if (frame.Message.Id > _lastStarted) _canceledBeforeStart.Add(frame.Message.Id);
                        }
                    }
                    else if (frame.Message.Type == TranscriptionWorkerMessageTypes.Request) _requests.Writer.TryWrite(frame);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ObjectDisposedException) { }
            finally { _requests.Writer.TryComplete(); }
        }

        private async Task<TranscriptionWorkerResult?> ExecuteAsync(TranscriptionWorkerMessage message, byte[] payload, CancellationToken ct)
        {
            switch (message.Command)
            {
                case TranscriptionWorkerCommands.Load:
                    var modelId = message.ModelId ?? throw new ArgumentException("A model is required.");
                    _loadedModelId = null;
                    await engine.LoadModelAsync(modelId, ct).ConfigureAwait(false);
                    _loadedModelId = modelId;
                    return null;
                case TranscriptionWorkerCommands.Transcribe:
                    await EnsureModelAsync(message.ModelId, ct).ConfigureAwait(false);
                    var result = message.AudioFormat switch
                    {
                        TranscriptionWorkerAudioFormats.Pcm when message.Prompt is { } prompt =>
                            await engine.TranscribePcmWithPromptAsync(ToSamples(payload), message.Language, message.Translate, prompt, ct).ConfigureAwait(false),
                        TranscriptionWorkerAudioFormats.Pcm => await engine.TranscribePcmAsync(ToSamples(payload), message.Language, message.Translate, ct).ConfigureAwait(false),
                        TranscriptionWorkerAudioFormats.Wav when message.LanguageHints is { } hints =>
                            await engine.TranscribeWithLanguageHintsAsync(payload, hints, message.Translate, message.Prompt, ct).ConfigureAwait(false),
                        TranscriptionWorkerAudioFormats.Wav => await engine.TranscribeAsync(payload, message.Language, message.Translate, message.Prompt, ct).ConfigureAwait(false),
                        _ => throw new NotSupportedException("Unknown audio format.")
                    };
                    return TranscriptionWorkerProtocol.ToResult(result);
                default:
                    throw new NotSupportedException("Unknown transcription worker command.");
            }
        }

        // A restarted worker starts without a model. Each request names the host's selection, so it
        // loads and selects exactly that model; plugins without a selection report their own error.
        private async Task EnsureModelAsync(string? modelId, CancellationToken ct)
        {
            if (modelId is null) return;
            if (_loadedModelId != modelId)
            {
                _loadedModelId = null;
                await engine.LoadModelAsync(modelId, ct).ConfigureAwait(false);
                _loadedModelId = modelId;
            }
            if (engine.SelectedModelId != modelId) await engine.SelectModelAsync(modelId, ct).ConfigureAwait(false);
        }

        private static float[] ToSamples(byte[] payload)
        {
            if (payload.Length % sizeof(float) != 0) throw new InvalidDataException("The audio payload is not 32-bit PCM.");
            return MemoryMarshal.Cast<byte, float>(payload).ToArray();
        }
    }
}

// The heartbeat is optional on both sides: a worker without the argument never beats, and a host that
// receives no beats never arms its watchdog, so either side may be older than the other.
internal sealed record TranscriptionWorkerArguments(string PipeName, int ParentProcessId, string PackageDirectory,
    string DataDirectory, string AssetDirectory, string SelectionId, TranscriptionAccelerationPreference Acceleration, Version HostVersion,
    TimeSpan? HeartbeatInterval = null)
{
    internal IEnumerable<string> ToArguments()
    {
        string[] arguments =
        [
            "--pipe", PipeName, "--parent-pid", ParentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--package", PackageDirectory, "--data", DataDirectory, "--assets", AssetDirectory, "--selection", SelectionId,
            "--acceleration", Acceleration.ToString(), "--host-version", HostVersion.ToString()
        ];
        return HeartbeatInterval is { } interval
            ? arguments.Concat(["--heartbeat", interval.ToString("c", System.Globalization.CultureInfo.InvariantCulture)])
            : arguments;
    }

    internal static bool TryParse(IReadOnlyList<string> args, out TranscriptionWorkerArguments options)
    {
        options = null!;
        if (!TranscriptionWorkerServer.IsWorkerInvocation(args) || args.Count % 2 != 1) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
            if (!values.TryAdd(args[index], args[index + 1])) return false;
        string Value(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new FormatException(name);
        try
        {
            options = new(Value("--pipe"), int.Parse(Value("--parent-pid"), System.Globalization.CultureInfo.InvariantCulture),
                Value("--package"), Value("--data"), Value("--assets"), Value("--selection"),
                Enum.Parse<TranscriptionAccelerationPreference>(Value("--acceleration")), Version.Parse(Value("--host-version")),
                values.ContainsKey("--heartbeat") ? TimeSpan.ParseExact(Value("--heartbeat"), "c", System.Globalization.CultureInfo.InvariantCulture) : null);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { return false; }
    }
}

// The worker only runs inference. It reads the host's saved plugin settings once and keeps its own
// writes in memory: the host process owns the settings file, and a second writer could lose updates.
internal sealed class TranscriptionWorkerHostServices(string dataDirectory, string assetDirectory, Action<PluginLogLevel, string> log) : IPluginHostServices
{
    private readonly object _sync = new();
    private readonly Dictionary<string, JsonElement> _settings = ReadSettings(Path.Combine(dataDirectory, "settings.json"));
    public string PluginDataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public string PluginAssetDirectory { get; } = Path.GetFullPath(assetDirectory);
    public bool AllowLegacyDataMigration => false;
    public string? ActiveAppName => null;
    public string? ActiveAppProcessName => null;
    public IReadOnlyList<string> AvailableProfileNames => [];
    public void Log(PluginLogLevel level, string message) => log(level, message);
    public void NotifyCapabilitiesChanged() { }
    public T? GetSetting<T>(string key)
    {
        lock (_sync) return _settings.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;
    }
    public void SetSetting<T>(string key, T value)
    {
        lock (_sync) _settings[key] = JsonSerializer.SerializeToElement(value);
    }
    public Task StoreSecretAsync(string key, string value) => throw new NotSupportedException("The transcription worker has no secret storage.");
    public Task<string?> LoadSecretAsync(string key) => throw new NotSupportedException("The transcription worker has no secret storage.");
    public Task DeleteSecretAsync(string key) => throw new NotSupportedException("The transcription worker has no secret storage.");
    public IPluginEventBus EventBus => throw new NotSupportedException("Event bus is not connected in the transcription worker.");
    public IPluginLocalization Localization => throw new NotSupportedException("Plugin localization is not connected in the transcription worker.");

    // Shares delete access so the host can still replace the file while the worker starts.
    private static Dictionary<string, JsonElement> ReadSettings(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
