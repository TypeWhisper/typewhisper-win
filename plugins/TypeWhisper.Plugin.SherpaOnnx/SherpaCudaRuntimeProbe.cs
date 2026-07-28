using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal sealed record CudaRuntimeProbeResult(bool Success, string? ErrorMessage);

internal interface ISherpaCudaRuntimeProbe
{
    Task<CudaRuntimeProbeResult> ProbeAsync(
        string modelId,
        string modelDirectory,
        string runtimeDirectory,
        CancellationToken cancellationToken);
}

internal sealed class SherpaCudaRuntimeProbe(
    string hostExecutablePath,
    string pluginDirectory,
    string pluginAssetDirectory) : ISherpaCudaRuntimeProbe
{
    internal const string ProbeEnvironmentVariable = "TYPEWHISPER_NATIVE_MODEL_PROBE";
    internal const string ProbeArgument = "--native-model-probe";
    internal const string PluginDirectoryArgument = "--plugin-directory";
    internal const string PluginAssetDirectoryArgument = "--plugin-asset-directory";
    internal const string ModelIdArgument = "--model-id";
    internal const string RuntimeDirectoryArgument = "--runtime-directory";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);
    private const string SuccessCacheFileName = ".typewhisper-cuda-probe-success";

    public async Task<CudaRuntimeProbeResult> ProbeAsync(
        string modelId,
        string modelDirectory,
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostExecutablePath) || !File.Exists(hostExecutablePath))
        {
            return new CudaRuntimeProbeResult(
                false,
                "CUDA safety probe could not find the TypeWhisper executable.");
        }

        if (!Directory.Exists(pluginDirectory)
            || !Directory.Exists(pluginAssetDirectory)
            || !Directory.Exists(modelDirectory)
            || !Directory.Exists(runtimeDirectory))
        {
            return new CudaRuntimeProbeResult(
                false,
                "CUDA safety probe could not find the plugin, model, or runtime directory.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string fingerprint;
        try
        {
            fingerprint = BuildCacheFingerprint(
                modelId,
                hostExecutablePath,
                pluginDirectory,
                modelDirectory,
                runtimeDirectory);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return new CudaRuntimeProbeResult(
                false,
                $"CUDA safety probe could not fingerprint the native runtime: {ex.Message}");
        }

        var successCachePath = Path.Join(runtimeDirectory, SuccessCacheFileName);
        if (IsCachedSuccess(successCachePath, fingerprint))
            return new CudaRuntimeProbeResult(true, null);

        var startInfo = new ProcessStartInfo
        {
            FileName = hostExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(hostExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
        };
        startInfo.Environment[ProbeEnvironmentVariable] = "1";
        AddArgument(startInfo, ProbeArgument);
        AddArgument(startInfo, PluginDirectoryArgument, pluginDirectory);
        AddArgument(startInfo, PluginAssetDirectoryArgument, pluginAssetDirectory);
        AddArgument(startInfo, ModelIdArgument, modelId);
        AddArgument(startInfo, RuntimeDirectoryArgument, runtimeDirectory);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new CudaRuntimeProbeResult(false, "CUDA safety probe process could not be started.");

            // Drain stderr while the child is running. Waiting first can
            // deadlock when a failed native load fills the redirected pipe.
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                return new CudaRuntimeProbeResult(
                    false,
                    $"CUDA safety probe did not finish within {ProbeTimeout.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                throw;
            }

            var standardError = await standardErrorTask;
            if (process.ExitCode == 0)
            {
                RecordCachedSuccess(successCachePath, fingerprint);
                return new CudaRuntimeProbeResult(true, null);
            }

            var unsignedExitCode = unchecked((uint)process.ExitCode);
            var detail = string.IsNullOrWhiteSpace(standardError)
                ? $"Native CUDA probe exited with code 0x{unsignedExitCode:X8}."
                : $"Native CUDA probe exited with code 0x{unsignedExitCode:X8}: {standardError.Trim()}";
            return new CudaRuntimeProbeResult(false, detail);
        }
        catch (Exception ex) when (
            ex is Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            return new CudaRuntimeProbeResult(false, $"CUDA safety probe failed: {ex.Message}");
        }
    }

    internal static bool IsProbeProcess =>
        string.Equals(
            Environment.GetEnvironmentVariable(ProbeEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    internal static string BuildCacheFingerprint(
        string modelId,
        string executablePath,
        string pluginPath,
        string modelPath,
        string runtimePath)
    {
        var fingerprint = new StringBuilder();
        fingerprint.AppendLine(modelId);
        fingerprint.AppendLine(Environment.OSVersion.VersionString);
        fingerprint.AppendLine(RuntimeInformation.OSArchitecture.ToString());
        fingerprint.AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        AppendFileFingerprint(fingerprint, executablePath);
        AppendDirectoryFingerprint(fingerprint, pluginPath, "*.dll", SearchOption.TopDirectoryOnly);
        AppendDirectoryFingerprint(fingerprint, modelPath, "*", SearchOption.AllDirectories);
        AppendDirectoryFingerprint(fingerprint, runtimePath, "*.dll", SearchOption.TopDirectoryOnly);
        AppendFileFingerprint(
            fingerprint,
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll"));

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())));
    }

    internal static bool IsCachedSuccess(string cachePath, string fingerprint)
    {
        try
        {
            return File.Exists(cachePath)
                && string.Equals(
                    File.ReadAllText(cachePath).Trim(),
                    fingerprint,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return false;
        }
    }

    internal static void RecordCachedSuccess(string cachePath, string fingerprint)
    {
        try
        {
            File.WriteAllText(cachePath, fingerprint);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            // A read-only runtime remains usable; it only loses the fast path.
        }
    }

    private static void AppendDirectoryFingerprint(
        StringBuilder fingerprint,
        string directory,
        string searchPattern,
        SearchOption searchOption)
    {
        fingerprint.AppendLine(Path.GetFullPath(directory));
        foreach (var file in Directory
                     .EnumerateFiles(directory, searchPattern, searchOption)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            AppendFileFingerprint(fingerprint, file);
        }
    }

    private static void AppendFileFingerprint(StringBuilder fingerprint, string path)
    {
        var resolvedPath = Path.GetFullPath(path);
        fingerprint.Append(resolvedPath);
        if (File.Exists(resolvedPath))
        {
            var info = new FileInfo(resolvedPath);
            fingerprint
                .Append('|')
                .Append(info.Length)
                .Append('|')
                .Append(info.LastWriteTimeUtc.Ticks);
        }
        else
        {
            fingerprint.Append("|missing");
        }

        fingerprint.AppendLine();
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string? value = null)
    {
        startInfo.ArgumentList.Add(name);
        if (value is not null)
            startInfo.ArgumentList.Add(value);
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
