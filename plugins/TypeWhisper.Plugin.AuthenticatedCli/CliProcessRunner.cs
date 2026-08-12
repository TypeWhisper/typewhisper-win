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
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const int ProcThreadAttributeHandleList = 0x00020002;
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
        using var job = WindowsJobObject.CreateKillOnClose();
        using var launched = StartSuspended(CreateStartInfo(request), job);
        var process = launched.Process;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        var inputTask = WriteInputAsync(launched.StandardInput, request.StandardInput, timeout.Token);
        var outputTask = ReadLimitedAsync(
            launched.StandardOutput,
            request.MaximumStandardOutputBytes,
            "stdout",
            timeout.Token);
        var errorTask = ReadLimitedAsync(
            launched.StandardError,
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
            job.Terminate();
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
                if (!CliPathSafety.IsSafeLocalDirectory(value))
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

    private static NativeLaunchedProcess StartSuspended(
        ProcessStartInfo startInfo,
        WindowsJobObject job)
    {
        try
        {
            return StartSuspendedCore(startInfo, job);
        }
        catch (PluginRequestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception
                                   or InvalidOperationException
                                   or ArgumentException
                                   or IOException)
        {
            throw CreateConfigurationFailure("The provider CLI could not be started safely.", ex);
        }
    }

    private static NativeLaunchedProcess StartSuspendedCore(
        ProcessStartInfo startInfo,
        WindowsJobObject job)
    {
        using var inputPipe = CreateNativePipe(parentReads: false);
        using var outputPipe = CreateNativePipe(parentReads: true);
        using var errorPipe = CreateNativePipe(parentReads: true);
        SafeFileHandle? processHandle = null;
        SafeFileHandle? primaryThreadHandle = null;
        FileStream? standardInput = null;
        FileStream? standardOutput = null;
        FileStream? standardError = null;
        Process? process = null;
        var attributeList = IntPtr.Zero;
        var inheritedHandleList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var attributeListInitialized = false;
        var assignedToJob = false;

        try
        {
            var attributeListSize = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref attributeListSize);
            if (attributeListSize == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size process attributes.");

            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize process attributes.");
            attributeListInitialized = true;

            inheritedHandleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(inheritedHandleList, 0, inputPipe.Child.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandleList, IntPtr.Size, outputPipe.Child.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandleList, 2 * IntPtr.Size, errorPipe.Child.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    new IntPtr(ProcThreadAttributeHandleList),
                    inheritedHandleList,
                    new IntPtr(3 * IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict inherited handles.");
            }

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = inputPipe.Child.DangerousGetHandle(),
                    StandardOutput = outputPipe.Child.DangerousGetHandle(),
                    StandardError = errorPipe.Child.DangerousGetHandle()
                },
                AttributeList = attributeList
            };
            var environment = BuildEnvironmentBlock(startInfo);
            environmentBlock = Marshal.StringToHGlobalUni(environment);
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var creationFlags = CreateSuspended
                                | CreateUnicodeEnvironment
                                | ExtendedStartupInfoPresent
                                | CreateNoWindow;

            if (!CreateProcessW(
                    startInfo.FileName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    creationFlags,
                    environmentBlock,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the provider process.");
            }

            processHandle = new SafeFileHandle(processInformation.Process, ownsHandle: true);
            primaryThreadHandle = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            inputPipe.Child.Dispose();
            outputPipe.Child.Dispose();
            errorPipe.Child.Dispose();

            job.Assign(processHandle.DangerousGetHandle());
            assignedToJob = true;
            VerifyStartedExecutable(processHandle.DangerousGetHandle(), startInfo.FileName);
            process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));

            standardInput = CreateParentStream(inputPipe, FileAccess.Write);
            standardOutput = CreateParentStream(outputPipe, FileAccess.Read);
            standardError = CreateParentStream(errorPipe, FileAccess.Read);

            if (ResumeThread(primaryThreadHandle.DangerousGetHandle()) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the provider process.");

            var launched = new NativeLaunchedProcess(process, standardInput, standardOutput, standardError);
            process = null;
            standardInput = null;
            standardOutput = null;
            standardError = null;
            return launched;
        }
        catch
        {
            if (assignedToJob)
                job.Terminate();
            else if (processHandle is { IsInvalid: false, IsClosed: false })
                _ = TerminateProcess(processHandle, 1);
            throw;
        }
        finally
        {
            standardInput?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            process?.Dispose();
            primaryThreadHandle?.Dispose();
            processHandle?.Dispose();
            if (attributeList != IntPtr.Zero)
            {
                if (attributeListInitialized)
                    DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandleList != IntPtr.Zero)
                Marshal.FreeHGlobal(inheritedHandleList);
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static NativePipePair CreateNativePipe(bool parentReads)
    {
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = 1
        };
        if (!CreatePipe(out var readHandle, out var writeHandle, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a redirected process pipe.");

        var parent = parentReads ? readHandle : writeHandle;
        var child = parentReads ? writeHandle : readHandle;
        if (!SetHandleInformation(parent, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            readHandle.Dispose();
            writeHandle.Dispose();
            throw new Win32Exception(error, "Could not protect a parent pipe handle.");
        }

        return new NativePipePair(parent, child);
    }

    private static FileStream CreateParentStream(NativePipePair pipe, FileAccess access)
    {
        var stream = new FileStream(pipe.Parent, access, 4096, isAsync: false);
        _ = pipe.TakeParent();
        return stream;
    }

    private static string BuildEnvironmentBlock(ProcessStartInfo startInfo) =>
        string.Concat(startInfo.Environment
            .Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}\0")) + "\0";

    private static string BuildCommandLine(ProcessStartInfo startInfo) =>
        // CreateProcessW requires one command-line buffer. Quote each ArgumentList entry
        // with Windows argv rules; no command shell participates in this conversion.
        string.Join(
            " ",
            startInfo.ArgumentList
                .Prepend(startInfo.FileName)
                .Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";
        if (!argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return argument;

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void VerifyStartedExecutable(IntPtr processHandle, string requestedPath)
    {
        var buffer = new StringBuilder(32_768);
        var length = buffer.Capacity;
        if (!QueryFullProcessImageNameW(processHandle, 0, buffer, ref length))
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
        Stream standardInput,
        string input,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = Utf8WithoutBom.GetBytes(input);
            await standardInput.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await standardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The process may fail before it consumes stdin. Exit status and output remain authoritative.
        }
        finally
        {
            try
            {
                standardInput.Close();
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        uint flags,
        StringBuilder executablePath,
        ref int executablePathLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort ReservedSize;
        internal IntPtr ReservedData;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    private sealed class NativePipePair(SafeFileHandle parent, SafeFileHandle child) : IDisposable
    {
        private SafeFileHandle? _parent = parent;

        internal SafeFileHandle Parent => _parent
            ?? throw new ObjectDisposedException(nameof(NativePipePair));

        internal SafeFileHandle Child { get; } = child;

        internal SafeFileHandle TakeParent()
        {
            var handle = Parent;
            _parent = null;
            return handle;
        }

        public void Dispose()
        {
            _parent?.Dispose();
            _parent = null;
            Child.Dispose();
        }
    }

    private sealed class NativeLaunchedProcess(
        Process process,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError) : IDisposable
    {
        internal Process Process { get; } = process;
        internal Stream StandardInput { get; } = standardInput;
        internal Stream StandardOutput { get; } = standardOutput;
        internal Stream StandardError { get; } = standardError;

        public void Dispose()
        {
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            Process.Dispose();
        }
    }

    private sealed class CliOutputLimitExceededException(string message) : IOException(message);
}

internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint MaximumActiveProcesses = 32;
    private const ulong MaximumJobMemoryBytes = 2UL * 1024 * 1024 * 1024;
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
                             | JobObjectLimitActiveProcess
                             | JobObjectLimitJobMemory,
                ActiveProcessLimit = MaximumActiveProcesses
            },
            JobMemoryLimit = new UIntPtr(MaximumJobMemoryBytes)
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

    internal void Assign(IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the provider CLI to cleanup control.");
    }

    internal void Terminate()
    {
        if (!_handle.IsInvalid && !_handle.IsClosed)
            _ = TerminateJobObject(_handle, 1);
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
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
