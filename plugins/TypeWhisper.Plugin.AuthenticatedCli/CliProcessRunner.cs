using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal sealed record CliProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string StandardInput,
    string WorkingDirectory,
    IReadOnlyList<string> ProviderEnvironmentVariables,
    TimeSpan Timeout,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes);

internal sealed record CliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed,
    int StandardOutputBytes,
    int StandardErrorBytes);

internal interface ICliProcessRunner
{
    Task<CliProcessResult> RunAsync(CliProcessRequest request, CancellationToken cancellationToken);
}

internal sealed class CliProcessRunner : ICliProcessRunner
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false, true);
    private static readonly string[] CommonEnvironmentVariables =
    [
        "SystemRoot",
        "WINDIR",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "APPDATA",
        "LOCALAPPDATA",
        "PROGRAMDATA",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR"
    ];

    public async Task<CliProcessResult> RunAsync(
        CliProcessRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        using var job = WindowsJobObject.CreateKillOnClose();

        try
        {
            if (!process.Start())
                throw CreateConfigurationFailure("The provider CLI could not be started.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw CreateConfigurationFailure("The provider CLI could not be started.", ex);
        }

        try
        {
            job.Assign(process);
            VerifyStartedExecutable(process, request.ExecutablePath);
        }
        catch
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            throw;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        var inputTask = WriteInputAsync(process, request.StandardInput, timeout.Token);
        var outputTask = ReadLimitedAsync(
            process.StandardOutput.BaseStream,
            request.MaximumStandardOutputBytes,
            "stdout",
            timeout.Token);
        var errorTask = ReadLimitedAsync(
            process.StandardError.BaseStream,
            request.MaximumStandardErrorBytes,
            "stderr",
            timeout.Token);
        var exitTask = process.WaitForExitAsync(timeout.Token);
        var failureTask = WatchForFailureAsync(inputTask, outputTask, errorTask);

        try
        {
            var first = await Task.WhenAny(exitTask, failureTask).ConfigureAwait(false);
            if (first == failureTask && await failureTask.ConfigureAwait(false) is { } streamFailure)
                throw streamFailure;

            await exitTask.ConfigureAwait(false);
            await inputTask.ConfigureAwait(false);
            var outputBytes = await outputTask.ConfigureAwait(false);
            var errorBytes = await errorTask.ConfigureAwait(false);
            stopwatch.Stop();

            return new CliProcessResult(
                process.ExitCode,
                Utf8WithoutBom.GetString(outputBytes),
                Utf8WithoutBom.GetString(errorBytes),
                stopwatch.Elapsed,
                outputBytes.Length,
                errorBytes.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            throw new PluginRequestException(
                "The provider CLI timed out.",
                PluginRequestFailureKind.Timeout,
                isTransient: false);
        }
        catch (CliOutputLimitExceededException ex)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            throw new PluginRequestException(
                "The provider CLI produced too much output.",
                PluginRequestFailureKind.Unknown,
                isTransient: false,
                innerException: ex);
        }
        catch (DecoderFallbackException ex)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            throw new PluginRequestException(
                "The provider CLI returned output that was not valid UTF-8.",
                PluginRequestFailureKind.Unknown,
                isTransient: false,
                innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            throw new PluginRequestException(
                "The provider CLI process failed.",
                PluginRequestFailureKind.Unknown,
                isTransient: false,
                innerException: ex);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(CliProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(request.ExecutablePath),
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom
        };

        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        foreach (var name in CommonEnvironmentVariables.Concat(request.ProviderEnvironmentVariables).Distinct())
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
                continue;

            if (name is "CODEX_HOME" or "CLAUDE_CONFIG_DIR")
            {
                if (!IsSafeLocalDirectory(value))
                    continue;
            }

            startInfo.Environment[name] = value;
        }

        var executableDirectory = Path.GetDirectoryName(startInfo.FileName)!;
        var systemRoot = startInfo.Environment.TryGetValue("SystemRoot", out var configuredSystemRoot)
                         && !string.IsNullOrWhiteSpace(configuredSystemRoot)
            ? configuredSystemRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(systemRoot, "System32");
        startInfo.Environment["PATH"] = $"{executableDirectory}{Path.PathSeparator}{system32}";
        startInfo.Environment["PATHEXT"] = ".EXE";
        startInfo.Environment["TEMP"] = startInfo.WorkingDirectory;
        startInfo.Environment["TMP"] = startInfo.WorkingDirectory;
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CI"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1";
        return startInfo;
    }

    private static bool IsSafeLocalDirectory(string value)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value))
                return false;

            var path = Path.GetFullPath(value);
            var root = Path.GetPathRoot(path);
            return !path.StartsWith("\\\\", StringComparison.Ordinal)
                   && root is not null
                   && new DriveInfo(root).DriveType != DriveType.Network
                   && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void VerifyStartedExecutable(Process process, string requestedPath)
    {
        var buffer = new StringBuilder(32_768);
        var length = buffer.Capacity;
        if (!QueryFullProcessImageNameW(process.Handle, 0, buffer, ref length))
        {
            throw CreateConfigurationFailure(
                "The started provider CLI could not be verified.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var actualPath = buffer.ToString(0, length);

        if (string.IsNullOrWhiteSpace(actualPath)
            || !string.Equals(
                NormalizeComparisonPath(actualPath),
                NormalizeComparisonPath(requestedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw CreateConfigurationFailure("The started provider CLI did not match the selected executable.");
        }
    }

    private static string NormalizeComparisonPath(string path)
    {
        var normalized = path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path[4..]
            : path;
        return Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static async Task WriteInputAsync(
        Process process,
        string input,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The process may fail before it consumes stdin. Exit status and output remain authoritative.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child may already have closed its side of the pipe.
            }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        int maximumBytes,
        string streamName,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();

            if (buffer.Length + read > maximumBytes)
                throw new CliOutputLimitExceededException($"{streamName} exceeded {maximumBytes} bytes.");

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Exception?> WatchForFailureAsync(params Task[] tasks)
    {
        var pending = tasks.ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            if (completed.IsFaulted)
                return completed.Exception?.GetBaseException();
            if (completed.IsCanceled)
                return new OperationCanceledException();
        }

        return null;
    }

    private static async Task StopProcessAsync(Process process, WindowsJobObject job)
    {
        job.Terminate();
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException)
        {
            // Job-object termination remains the primary cleanup path.
        }

        try
        {
            using var shutdown = new CancellationTokenSource(ShutdownTimeout);
            await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // Cleanup must not hide the original request outcome.
        }
    }

    private static PluginRequestException CreateConfigurationFailure(string message, Exception? inner = null) =>
        new(
            message,
            PluginRequestFailureKind.Configuration,
            isTransient: false,
            innerException: inner);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        uint flags,
        StringBuilder executablePath,
        ref int executablePathLength);

    private sealed class CliOutputLimitExceededException(string message) : IOException(message);
}

internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private readonly SafeFileHandle _handle;

    private WindowsJobObject(SafeFileHandle handle)
    {
        _handle = handle;
    }

    internal static WindowsJobObject CreateKillOnClose()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a process job object.");

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var length = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref information,
                length))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not configure process-tree cleanup.");
        }

        return new WindowsJobObject(handle);
    }

    internal void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the provider CLI to cleanup control.");
    }

    internal void Terminate()
    {
        if (!_handle.IsInvalid && !_handle.IsClosed)
            _ = TerminateJobObject(_handle, 1);
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }
}
