using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal static class SherpaOnnxNativeRuntime
{
    private const string SherpaNativeLibraryBaseName = "sherpa-onnx-c-api";
    private const string SherpaNativeLibraryFileName = "sherpa-onnx-c-api.dll";
    private const string SherpaOnnxRuntimeDependencyFileName = "sherpaort.dll";
    private const DllImportSearchPath SherpaImportSearchPath =
        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories;

    private static readonly object Sync = new();
    private static bool _resolverRegistered;
    private static string? _bundledRuntimeDirectory;

    /// <summary>
    /// Loads sherpa-onnx from the plugin's own runtimes folder, whose sherpa-onnx-c-api.dll imports the renamed
    /// sherpaort.dll so it cannot pick up another plugin's onnxruntime.dll.
    /// </summary>
    public static void RegisterResolver()
    {
        lock (Sync)
        {
            _bundledRuntimeDirectory ??= ResolveBundledRuntimeDirectory(
                typeof(SherpaOnnxNativeRuntime).Assembly.Location,
                RuntimeInformation.ProcessArchitecture);

            if (_resolverRegistered)
                return;

            NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, ResolveNativeLibrary);
            _resolverRegistered = true;
        }
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!IsSherpaNativeLibrary(libraryName))
            return IntPtr.Zero;

        var runtimeDirectory = _bundledRuntimeDirectory;
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            throw new DllNotFoundException("Unable to determine the sherpa-onnx native runtime directory.");

        var candidate = Path.Join(runtimeDirectory, SherpaNativeLibraryFileName);
        var onnxRuntime = Path.Join(runtimeDirectory, SherpaOnnxRuntimeDependencyFileName);
        if (!File.Exists(candidate) || !File.Exists(onnxRuntime))
            throw new DllNotFoundException(CreateMissingRuntimeMessage(runtimeDirectory));

        try
        {
            return NativeLibrary.Load(
                candidate,
                typeof(SherpaOnnxNativeRuntime).Assembly,
                SherpaImportSearchPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or FileLoadException)
        {
            throw new DllNotFoundException(
                $"{CreateMissingRuntimeMessage(runtimeDirectory)} Loader error: {ex.Message}",
                ex);
        }
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
        + $"{SherpaNativeLibraryFileName} and {SherpaOnnxRuntimeDependencyFileName} under '{runtimeDirectory}'. "
        + "Reinstall the sherpa-onnx plugin or update it from the plugin marketplace.";

    private static bool IsSherpaNativeLibrary(string libraryName)
    {
        var fileName = Path.GetFileNameWithoutExtension(libraryName);
        return string.Equals(fileName, SherpaNativeLibraryBaseName, StringComparison.OrdinalIgnoreCase);
    }
}
