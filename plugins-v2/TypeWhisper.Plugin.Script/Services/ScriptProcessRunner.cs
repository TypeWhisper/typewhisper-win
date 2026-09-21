using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Script;

internal sealed class ScriptProcessRunner : IScriptProcessRunner
{
    internal const int MaximumStandardOutputBytes = 1024 * 1024;
    internal const int MaximumStandardErrorBytes = 64 * 1024;
    private static readonly TimeSpan s_shutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly Encoding s_utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<ScriptExecutionResult> RunAsync(
        ScriptEntry script,
        string input,
        PostProcessingContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var readyMarker = "TypeWhisperReady" + Guid.NewGuid().ToString("N");
        using var process = new Process { StartInfo = CreateStartInfo(script, context, readyMarker) };

        WindowsProcessJob? job = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            // The shell waits for a stdin handshake before executing user code.
            // Assign containment first so even an immediately exiting parent cannot orphan children.
            job = WindowsProcessJob.CreateAndAssign(process);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            job?.Dispose();
            stopwatch.Stop();
            return new ScriptExecutionResult(
                ScriptExecutionStatus.StartFailed, "", ex.Message, null, stopwatch.Elapsed);
        }

        using var jobLifetime = job;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(NormalizeTimeout(script.TimeoutSeconds)));

        try
        {
            // Wait for acknowledgement before sending input: cmd set /p can otherwise
            // prefetch and discard text intended for the user command.
            await StartContainedScriptAsync(process, script, readyMarker, timeout.Token).ConfigureAwait(false);
            var inputTask = WriteInputAsync(process, input, timeout.Token);
            var outputTask = ReadLimitedAsync(
                process.StandardOutput.BaseStream, MaximumStandardOutputBytes, "stdout", timeout.Token);
            var errorTask = ReadLimitedAsync(
                process.StandardError.BaseStream, MaximumStandardErrorBytes, "stderr", timeout.Token);
            var exitTask = process.WaitForExitAsync(timeout.Token);
            var failureTask = WatchForFailureAsync(inputTask, outputTask, errorTask);

            var first = await Task.WhenAny(exitTask, failureTask).ConfigureAwait(false);
            if (first == failureTask && await failureTask.ConfigureAwait(false) is { } streamFailure)
                throw streamFailure;

            await exitTask.ConfigureAwait(false);
            job.Dispose(); // Close inherited pipes held by descendants before awaiting EOF.
            await inputTask.ConfigureAwait(false);
            var output = Encoding.UTF8.GetString(await outputTask.ConfigureAwait(false));
            var error = Encoding.UTF8.GetString(await errorTask.ConfigureAwait(false));
            stopwatch.Stop();

            return process.ExitCode == 0
                ? new ScriptExecutionResult(
                    ScriptExecutionStatus.Success, output, error, process.ExitCode, stopwatch.Elapsed)
                : new ScriptExecutionResult(
                    ScriptExecutionStatus.Failed, output, error, process.ExitCode, stopwatch.Elapsed);
        }
        catch (OutputLimitExceededException ex)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            stopwatch.Stop();
            return new ScriptExecutionResult(
                ScriptExecutionStatus.OutputLimitExceeded, "", ex.Message, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            stopwatch.Stop();
            return new ScriptExecutionResult(
                ScriptExecutionStatus.TimedOut,
                "",
                $"Timed out after {NormalizeTimeout(script.TimeoutSeconds)} seconds.",
                null,
                stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await StopProcessAsync(process, job).ConfigureAwait(false);
            stopwatch.Stop();
            return new ScriptExecutionResult(
                ScriptExecutionStatus.Failed, "", ex.Message, null, stopwatch.Elapsed);
        }
    }

    private static ProcessStartInfo CreateStartInfo(ScriptEntry script, PostProcessingContext context, string readyMarker)
    {
        var shell = ScriptShells.Normalize(script.Shell);
        var startInfo = new ProcessStartInfo
        {
            FileName = shell switch
            {
                ScriptShells.WindowsPowerShell => "powershell.exe",
                ScriptShells.PowerShell => "pwsh.exe",
                _ => "cmd.exe"
            },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = s_utf8WithoutBom,
            StandardOutputEncoding = s_utf8WithoutBom,
            StandardErrorEncoding = s_utf8WithoutBom
        };

        if (shell == ScriptShells.CommandPrompt || !ScriptShells.IsSupported(shell))
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/v:off");
            startInfo.ArgumentList.Add("/c");
        }
        else
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
        }

        var command = shell == ScriptShells.CommandPrompt
            ? ">nul set /p \"__TYPEWHISPER_START=\" & >&2 echo " + readyMarker + " & " + script.Command
            : "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
              "$encodedScript = [Console]::In.ReadLine(); [Console]::Error.WriteLine('" + readyMarker + "'); & ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encodedScript))))";
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["TYPEWHISPER_APP_NAME"] = context.ActiveAppName ?? "";
        startInfo.Environment["TYPEWHISPER_LANGUAGE"] = context.SourceLanguage ?? "";
        startInfo.Environment["TYPEWHISPER_PROFILE"] = context.ProfileName ?? "";
        return startInfo;
    }

    private static int NormalizeTimeout(int timeoutSeconds) =>
        timeoutSeconds is >= ScriptDefaults.MinimumTimeoutSeconds and <= ScriptDefaults.MaximumTimeoutSeconds
            ? timeoutSeconds
            : ScriptDefaults.TimeoutSeconds;

    private static async Task StartContainedScriptAsync(Process process, ScriptEntry script, string marker, CancellationToken cancellationToken)
    {
        // Transport PowerShell source over stdin, keeping Unicode scripts within the Windows command-line limit.
        var payload = ScriptShells.Normalize(script.Shell) == ScriptShells.CommandPrompt ? "start"
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Command));
        await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        var response = new StringBuilder();
        var singleByte = new byte[1];
        while (response.Length <= marker.Length + 4)
        {
            if (await process.StandardError.BaseStream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false) == 0)
                throw new IOException("The script shell exited before acknowledging startup.");
            if (singleByte[0] == (byte)'\n')
            {
                if (response.ToString().Trim() == marker) return;
                break;
            }
            response.Append((char)singleByte[0]);
        }
        throw new IOException("The script shell did not acknowledge startup.");
    }

    private static async Task WriteInputAsync(Process process, string input, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A command may exit successfully without consuming stdin. A closed input pipe is
            // therefore not a script failure; stdout, stderr, and the exit code remain authoritative.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child may have closed its end of the pipe before the parent closes the writer.
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
                throw new OutputLimitExceededException($"{streamName} exceeded {maximumBytes} bytes.");

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

    private static async Task StopProcessAsync(Process process, WindowsProcessJob? job)
    {
        job?.Dispose(); // Closes descendants even if the shell has already exited.
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Best effort shutdown.
        }

        try
        {
            using var shutdown = new CancellationTokenSource(s_shutdownTimeout);
            await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // Do not let process cleanup hide the original outcome.
        }
    }

    private sealed class OutputLimitExceededException(string message) : IOException(message);
}
