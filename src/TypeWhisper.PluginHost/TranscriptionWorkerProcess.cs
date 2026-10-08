using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>The worker process ended or stopped answering; the request may be retried in a new worker.</summary>
public class TranscriptionWorkerCrashedException(string message, int? exitCode, string diagnostics, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The worker's exit code, when it could be observed.</summary>
    public int? ExitCode { get; } = exitCode;
    /// <summary>The last lines the worker and its native libraries wrote to stdout and stderr.</summary>
    public string Diagnostics { get; } = diagnostics;
}

/// <summary>
/// The worker stayed silent for a whole inactivity window during a request and was ended by the host.
/// It is a crash for the restart policy; the message tells the user that nothing actually crashed.
/// </summary>
public sealed class TranscriptionWorkerUnresponsiveException(string message, string diagnostics)
    : TranscriptionWorkerCrashedException(message, null, diagnostics);

/// <summary>A worker exceeded its total request budget, even if its heartbeat still ran.</summary>
public sealed class TranscriptionWorkerRequestTimeoutException(string message) : TimeoutException(message);

/// <summary>One running worker. Requests are answered in order; the owner sends one at a time.</summary>
internal interface ITranscriptionWorkerConnection : IAsyncDisposable
{
    TranscriptionAccelerationPreference Acceleration { get; }
    int ProcessId { get; }
    bool IsAlive { get; }
    TranscriptionWorkerState? State { get; }
    /// <summary>Returns the worker's answer, including engine errors. Throws <see cref="TranscriptionWorkerCrashedException"/> if the worker ends.</summary>
    Task<TranscriptionWorkerMessage> SendAsync(TranscriptionWorkerMessage request, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

internal sealed record TranscriptionWorkerLaunch(string ExecutablePath, IReadOnlyList<string> PrefixArguments, string PackageDirectory,
    string DataDirectory, string AssetDirectory, string SelectionId, Version HostVersion)
{
    // A beat is one tiny frame, so a short interval costs nothing; the window is what the user waits
    // when a worker froze. It is silence, not work, that is measured: a slow decode or a model load
    // that reads gigabytes keeps beating, so neither needs a longer window. 90 s is 18 missed beats,
    // enough to ride out a long garbage collection or a starved thread pool without ending a worker
    // that is only busy.
    internal static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultRequestInactivityTimeout = TimeSpan.FromSeconds(90);

    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How often the worker reports that a request is still running.</summary>
    internal TimeSpan HeartbeatInterval { get; init; } = DefaultHeartbeatInterval;
    /// <summary>How long a worker may stay silent during a request before it is ended; <see cref="Timeout.InfiniteTimeSpan"/> disables the watchdog.</summary>
    internal TimeSpan RequestInactivityTimeout { get; init; } = DefaultRequestInactivityTimeout;
    internal TimeSpan? RequestTimeout { get; init; }
    internal bool WatchesInactivity => RequestInactivityTimeout != Timeout.InfiniteTimeSpan;

    internal TimeSpan TimeoutFor(TranscriptionWorkerMessage request, int payloadBytes)
    {
        if (RequestTimeout is { } configured)
        {
            if (configured != Timeout.InfiniteTimeSpan && configured <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
            return configured;
        }
        if (request.Command == TranscriptionWorkerCommands.Load) return TimeSpan.FromMinutes(10);
        if (request.Command != TranscriptionWorkerCommands.Transcribe) return TimeSpan.FromMinutes(1);
        // PCM transport is 16 kHz mono float32; the WAV path normally uses PCM16.
        // Counting WAV headers as audio only increases the budget slightly.
        var bytesPerSecond = request.AudioFormat == TranscriptionWorkerAudioFormats.Pcm ? 16000 * 4d : 16000 * 2d;
        return TimeSpan.FromSeconds(120 + 10d * payloadBytes / bytesPerSecond);
    }
}

internal sealed class TranscriptionWorkerProcess : ITranscriptionWorkerConnection
{
    private const int DiagnosticLines = 60;
    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly KillOnCloseJob? _job;
    private readonly TranscriptionWorkerLaunch _launch;
    private readonly Action<PluginLogLevel, string> _log;
    private readonly ConcurrentQueue<string> _output = new();
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _reader = Task.CompletedTask;
    private long _nextId;
    private int _disposed;
    private bool _heartbeats;

    public TranscriptionAccelerationPreference Acceleration { get; }
    public int ProcessId { get; }
    public bool IsAlive => !_ended.Task.IsCompleted && !HasExited();
    public TranscriptionWorkerState? State { get; private set; }

    private TranscriptionWorkerProcess(Process process, NamedPipeServerStream pipe, KillOnCloseJob? job, TranscriptionWorkerLaunch launch,
        TranscriptionAccelerationPreference acceleration, Action<PluginLogLevel, string> log)
    {
        _process = process; _pipe = pipe; _job = job; _launch = launch; _log = log;
        Acceleration = acceleration;
        ProcessId = process.Id;
    }

    internal static async Task<TranscriptionWorkerProcess> StartAsync(TranscriptionWorkerLaunch launch,
        TranscriptionAccelerationPreference acceleration, Action<PluginLogLevel, string> log, CancellationToken ct)
    {
        // One late beat must not end a worker; the window has to hold several.
        if (launch.WatchesInactivity && launch.RequestInactivityTimeout < launch.HeartbeatInterval * 2)
            throw new ArgumentException("The inactivity window must be at least twice the heartbeat interval.", nameof(launch));
        var arguments = new TranscriptionWorkerArguments("TypeWhisper.Transcription." + Guid.NewGuid().ToString("N"), Environment.ProcessId,
            launch.PackageDirectory, launch.DataDirectory, launch.AssetDirectory, launch.SelectionId, acceleration, launch.HostVersion,
            launch.WatchesInactivity ? launch.HeartbeatInterval : null);
        var pipe = new NamedPipeServerStream(arguments.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = new ProcessStartInfo(launch.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(launch.ExecutablePath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in launch.PrefixArguments.Concat(arguments.ToArguments())) info.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("The transcription worker could not be started."); }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        var job = KillOnCloseJob.TryAssign(process);
        var worker = new TranscriptionWorkerProcess(process, pipe, job, launch, acceleration, log);
        try
        {
            worker.CaptureOutput();
            await worker.ConnectAsync(ct).ConfigureAwait(false);
            return worker;
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void CaptureOutput()
    {
        void Capture(object _, DataReceivedEventArgs line)
        {
            if (line.Data is null) return;
            _output.Enqueue(line.Data);
            while (_output.Count > DiagnosticLines) _output.TryDequeue(out string? _);
        }
        _process.OutputDataReceived += Capture;
        _process.ErrorDataReceived += Capture;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(_launch.StartupTimeout);
        try
        {
            // A worker that exits before connecting never completes the wait; after connecting,
            // its exit closes the pipe and ends the read below.
            var connected = _pipe.WaitForConnectionAsync(startup.Token);
            if (await Task.WhenAny(connected, _process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false) != connected)
                throw await CrashAsync("The local transcription engine ended while starting.").ConfigureAwait(false);
            await connected.ConfigureAwait(false);
            (TranscriptionWorkerMessage Message, byte[] Payload) frame;
            // Plugins may log while they activate, before the worker reports that it is ready.
            while ((frame = await TranscriptionWorkerProtocol.ReadAsync(_pipe, startup.Token).ConfigureAwait(false)
                ?? throw await CrashAsync("The local transcription engine ended while starting.").ConfigureAwait(false)).Message.Type == TranscriptionWorkerMessageTypes.Log)
                if (frame.Message.LogMessage is { } text) _log(frame.Message.LogLevel ?? PluginLogLevel.Info, text);
            if (frame.Message.Type != TranscriptionWorkerMessageTypes.Hello || frame.Message.ProtocolVersion != TranscriptionWorkerProtocol.Version)
                throw new InvalidDataException("The transcription worker does not match this TypeWhisper version.");
            // Activation errors are the plugin's own and repeat on every start; they are not crashes.
            if (frame.Message.Error is { } error) throw TranscriptionWorkerProtocol.ToException(error);
            State = frame.Message.State;
            // A worker that announces no beat (an older build) is never ended for silence.
            _heartbeats = frame.Message.HeartbeatInterval is not null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw await CrashAsync("The local transcription engine did not start in time.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException && !ct.IsCancellationRequested)
        {
            throw await CrashAsync("The local transcription engine ended while starting.", ex).ConfigureAwait(false);
        }
        _reader = Task.Run(ReadAsync);
    }

    public async Task<TranscriptionWorkerMessage> SendAsync(TranscriptionWorkerMessage request, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var timeout = _launch.TimeoutFor(request, payload.Length);
        using var deadline = new CancellationTokenSource(timeout);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = requestCancellation.Token;
        var id = Interlocked.Increment(ref _nextId);
        var pending = new PendingRequest();
        _pending[id] = pending;
        try
        {
            if (_ended.Task.IsCompleted) throw await CrashAsync("The local transcription engine stopped unexpectedly.").ConfigureAwait(false);
            try { await WriteAsync(request with { Type = TranscriptionWorkerMessageTypes.Request, Id = id }, payload, token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // A partially written frame cannot be reused. Ending the worker also releases
                // a blocked pipe write, so a frozen reader cannot hold the engine's gate forever.
                _pending.TryRemove(id, out _); Kill(); throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            { throw await CrashAsync("The local transcription engine stopped unexpectedly.", ex).ConfigureAwait(false); }
            var response = await WaitAsync(pending, id, request.Command, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (response.State is { } state) State = state;
            return response;
        }
        catch (Exception ex) when ((ex is OperationCanceledException or TranscriptionWorkerCrashedException)
            && deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _); Kill();
            _log(PluginLogLevel.Warning, $"The transcription worker exceeded the {request.Command} deadline of {timeout.TotalSeconds:0} s and was ended.");
            throw new TranscriptionWorkerRequestTimeoutException("The local transcription engine took too long and was stopped. Try again or choose another model.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    // Native decoding may not stop at once. After a cancellation the worker gets a short grace period
    // to finish; one that does not answer is ended, so cancelling never waits for a long decode.
    private async Task<TranscriptionWorkerMessage> WaitAsync(PendingRequest pending, long id, string? command, CancellationToken ct)
    {
        var answer = pending.Answer.Task;
        try { return await WatchAsync(pending, id, command, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !answer.IsCompleted)
        {
            using var cancelWrite = new CancellationTokenSource(_launch.CancellationGrace);
            try { await WriteAsync(new() { Type = TranscriptionWorkerMessageTypes.Cancel, Id = id }, default, cancelWrite.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { Kill(); }
            try { await answer.WaitAsync(_launch.CancellationGrace, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or TranscriptionWorkerCrashedException)
            {
                _log(PluginLogLevel.Info, "The transcription worker did not stop after cancellation and was ended.");
                Kill();
            }
            throw new OperationCanceledException(ct);
        }
    }

    // Heartbeats distinguish a frozen process from slow inference. The request token also carries
    // the total deadline, because native inference can hang while the heartbeat remains responsive.
    private async Task<TranscriptionWorkerMessage> WatchAsync(PendingRequest pending, long id, string? command, CancellationToken ct)
    {
        var answer = pending.Answer.Task;
        if (!_heartbeats || !_launch.WatchesInactivity) return await answer.WaitAsync(ct).ConfigureAwait(false);
        var window = _launch.RequestInactivityTimeout;
        for (var remaining = window - pending.Silence; remaining > TimeSpan.Zero; remaining = window - pending.Silence)
        {
            try { return await answer.WaitAsync(remaining, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        // An answer that lands on the window's last tick is still an answer.
        if (answer.IsCompleted) return await answer.ConfigureAwait(false);
        _log(PluginLogLevel.Warning, $"The transcription worker did not answer the {command} request for {window.TotalSeconds:0} s and was ended.");
        // The reader must not report the end as a crash of this request; the exit code would be the host's own.
        _pending.TryRemove(id, out _);
        Kill();
        throw new TranscriptionWorkerUnresponsiveException("The local transcription engine stopped answering and was ended.", Diagnostics);
    }

    private async Task WriteAsync(TranscriptionWorkerMessage message, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await TranscriptionWorkerProtocol.WriteAsync(_pipe, message, payload, ct).ConfigureAwait(false); }
        finally { _write.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            while (await TranscriptionWorkerProtocol.ReadAsync(_pipe, CancellationToken.None).ConfigureAwait(false) is { } frame)
            {
                var message = frame.Message;
                if (message.Type == TranscriptionWorkerMessageTypes.Log && message.LogMessage is { } text)
                    _log(message.LogLevel ?? PluginLogLevel.Info, text);
                else if (message.Type == TranscriptionWorkerMessageTypes.Response && _pending.TryGetValue(message.Id, out var pending))
                    pending.Answer.TrySetResult(message);
                else if (message.Type == TranscriptionWorkerMessageTypes.Heartbeat && _pending.TryGetValue(message.Id, out pending))
                    pending.Touch();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ObjectDisposedException) { failure = ex; }
        _ended.TrySetResult();
        if (_pending.IsEmpty) return;
        var crash = await CrashAsync("The local transcription engine stopped unexpectedly.", failure).ConfigureAwait(false);
        foreach (var pending in _pending.Values) pending.Answer.TrySetException(crash);
    }

    private string Diagnostics => string.Join(Environment.NewLine, _output);

    private async Task<TranscriptionWorkerCrashedException> CrashAsync(string message, Exception? inner = null)
    {
        int? exitCode = null;
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            exitCode = _process.ExitCode;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
        var code = exitCode is { } value ? $" (exit code 0x{value:X8})" : "";
        return new(message + code, exitCode, Diagnostics, inner);
    }

    private bool HasExited()
    {
        try { return _process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (IsAlive && _reader != Task.CompletedTask)
        {
            // A graceful stop lets the engine exit on its own. It is short: native teardown is not awaited.
            try
            {
                await WriteAsync(new() { Type = TranscriptionWorkerMessageTypes.Request, Id = Interlocked.Increment(ref _nextId),
                    Command = TranscriptionWorkerCommands.Shutdown }, default).ConfigureAwait(false);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException) { }
        }
        Kill();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _job?.Dispose();
        _process.Dispose();
        _write.Dispose();
    }

    // The answer and the time of the last frame the worker sent for this request.
    private sealed class PendingRequest
    {
        private long _lastFrame = ActiveMilliseconds();
        internal TaskCompletionSource<TranscriptionWorkerMessage> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan Silence => TimeSpan.FromMilliseconds(ActiveMilliseconds() - Volatile.Read(ref _lastFrame));
        internal void Touch() => Volatile.Write(ref _lastFrame, ActiveMilliseconds());

        // .NET 10 TickCount64 includes Windows sleep time, while WaitAsync's timer excludes it.
        // Use the same awake-time basis so resume does not turn a live worker into a crash.
        private static long ActiveMilliseconds()
        {
            if (!OperatingSystem.IsWindows()) return Environment.TickCount64;
            QueryUnbiasedInterruptTime(out var ticks);
            return (long)(ticks / TimeSpan.TicksPerMillisecond);
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
    }
}

// Ends the worker with the host even if the host itself crashes: closing the last job handle kills it.
internal sealed class KillOnCloseJob : IDisposable
{
    private readonly IntPtr _handle;
    private KillOnCloseJob(IntPtr handle) => _handle = handle;

    internal static KillOnCloseJob? TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;
        var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (SetInformationJobObject(handle, 9, ref limits, (uint)size) && AssignProcessToJobObject(handle, process.Handle))
            return new(handle);
        CloseHandle(handle);
        return null;
    }

    public void Dispose() => CloseHandle(_handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
