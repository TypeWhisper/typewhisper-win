using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>The worker process ended or stopped answering; the request may be retried in a new worker.</summary>
public sealed class TranscriptionWorkerCrashedException(string message, int? exitCode, string diagnostics, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The worker's exit code, when it could be observed.</summary>
    public int? ExitCode { get; } = exitCode;
    /// <summary>The last lines the worker and its native libraries wrote to stdout and stderr.</summary>
    public string Diagnostics { get; } = diagnostics;
}

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
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(3);
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
    private readonly ConcurrentDictionary<long, TaskCompletionSource<TranscriptionWorkerMessage>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _reader = Task.CompletedTask;
    private long _nextId;
    private int _disposed;

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
        var arguments = new TranscriptionWorkerArguments("TypeWhisper.Transcription." + Guid.NewGuid().ToString("N"), Environment.ProcessId,
            launch.PackageDirectory, launch.DataDirectory, launch.AssetDirectory, launch.SelectionId, acceleration, launch.HostVersion);
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
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<TranscriptionWorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (_ended.Task.IsCompleted) throw await CrashAsync("The local transcription engine stopped unexpectedly.").ConfigureAwait(false);
            try { await WriteAsync(request with { Type = TranscriptionWorkerMessageTypes.Request, Id = id }, payload).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            { throw await CrashAsync("The local transcription engine stopped unexpectedly.", ex).ConfigureAwait(false); }
            var response = await WaitAsync(answer.Task, id, ct).ConfigureAwait(false);
            if (response.State is { } state) State = state;
            return response;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    // Native decoding may not stop at once. After a cancellation the worker gets a short grace period
    // to finish; one that does not answer is ended, so cancelling never waits for a long decode.
    private async Task<TranscriptionWorkerMessage> WaitAsync(Task<TranscriptionWorkerMessage> answer, long id, CancellationToken ct)
    {
        try { return await answer.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !answer.IsCompleted)
        {
            try { await WriteAsync(new() { Type = TranscriptionWorkerMessageTypes.Cancel, Id = id }, default).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { await answer.WaitAsync(_launch.CancellationGrace, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or TranscriptionWorkerCrashedException)
            {
                _log(PluginLogLevel.Info, "The transcription worker did not stop after cancellation and was ended.");
                Kill();
            }
            throw new OperationCanceledException(ct);
        }
    }

    private async Task WriteAsync(TranscriptionWorkerMessage message, ReadOnlyMemory<byte> payload)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try { await TranscriptionWorkerProtocol.WriteAsync(_pipe, message, payload, CancellationToken.None).ConfigureAwait(false); }
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
                else if (message.Type == TranscriptionWorkerMessageTypes.Response && _pending.TryGetValue(message.Id, out var answer))
                    answer.TrySetResult(message);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ObjectDisposedException) { failure = ex; }
        _ended.TrySetResult();
        if (_pending.IsEmpty) return;
        var crash = await CrashAsync("The local transcription engine stopped unexpectedly.", failure).ConfigureAwait(false);
        foreach (var answer in _pending.Values) answer.TrySetException(crash);
    }

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
        return new(message + code, exitCode, string.Join(Environment.NewLine, _output), inner);
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
