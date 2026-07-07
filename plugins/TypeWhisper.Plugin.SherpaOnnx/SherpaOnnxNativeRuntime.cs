using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal static class SherpaOnnxNativeRuntime
{
    private const string SherpaNativeLibraryBaseName = "sherpa-onnx-c-api";
    private const string SherpaNativeLibraryFileName = "sherpa-onnx-c-api.dll";
    private const string OnnxRuntimeFileName = "onnxruntime.dll";
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    private static readonly object Sync = new();
    private static bool _resolverRegistered;
    private static string? _bundledRuntimeDirectory;
    private static string? _cudaRuntimeDirectory;

    /// <summary>
    /// Performs register resolver.
    /// </summary>
    public static void RegisterResolver()
    {
        lock (Sync)
            RegisterResolverUnsafe();
    }

    /// <summary>
    /// Performs configure cuda runtime.
    /// </summary>
    public static void ConfigureCudaRuntime(string runtimeDirectory)
    {
        lock (Sync)
        {
            RegisterResolverUnsafe();
            _cudaRuntimeDirectory = runtimeDirectory;
            PrependToPath(runtimeDirectory);
        }
    }

    private static void RegisterResolverUnsafe()
    {
        _bundledRuntimeDirectory ??= ResolveBundledRuntimeDirectory(
            typeof(SherpaOnnxNativeRuntime).Assembly.Location,
            RuntimeInformation.ProcessArchitecture);

        if (_resolverRegistered)
            return;

        NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, ResolveNativeLibrary);
        _resolverRegistered = true;
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!IsSherpaNativeLibrary(libraryName))
            return IntPtr.Zero;

        var runtimeDirectory = _cudaRuntimeDirectory ?? _bundledRuntimeDirectory;
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            throw new DllNotFoundException("Unable to determine the sherpa-onnx native runtime directory.");

        var candidate = Path.Join(runtimeDirectory, SherpaNativeLibraryFileName);
        var onnxRuntime = Path.Join(runtimeDirectory, OnnxRuntimeFileName);
        if (!File.Exists(candidate) || !File.Exists(onnxRuntime))
            throw new DllNotFoundException(CreateMissingRuntimeMessage(runtimeDirectory));

        var handle = LoadLibraryEx(
            candidate,
            IntPtr.Zero,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new DllNotFoundException(
                $"{CreateMissingRuntimeMessage(runtimeDirectory)} Windows loader error: {error}.");
        }

        return handle;
    }

    internal static string? ResolveBundledRuntimeDirectory(string assemblyLocation, Architecture architecture)
    {
        var runtimeIdentifier = architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => null
        };

        if (runtimeIdentifier is null || string.IsNullOrWhiteSpace(assemblyLocation))
            return null;

        var pluginDirectory = Path.GetDirectoryName(assemblyLocation);
        return string.IsNullOrWhiteSpace(pluginDirectory)
            ? null
            : Path.Join(pluginDirectory, "runtimes", runtimeIdentifier, "native");
    }

    internal static string CreateMissingRuntimeMessage(string runtimeDirectory) =>
        "Unable to load the sherpa-onnx native runtime. Expected "
        + $"{SherpaNativeLibraryFileName} and {OnnxRuntimeFileName} under '{runtimeDirectory}'. "
        + "Reinstall the sherpa-onnx plugin or update it from the plugin marketplace.";

    private static bool IsSherpaNativeLibrary(string libraryName)
    {
        var fileName = Path.GetFileNameWithoutExtension(libraryName);
        return string.Equals(fileName, SherpaNativeLibraryBaseName, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrependToPath(string runtimeDirectory)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry => string.Equals(entry, runtimeDirectory, StringComparison.OrdinalIgnoreCase)))
            return;

        Environment.SetEnvironmentVariable("PATH", runtimeDirectory + Path.PathSeparator + current);
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);
}
