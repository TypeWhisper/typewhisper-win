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
    private const string SherpaOnnxRuntimeDependencyFileName = "sherpaort.dll";
    private const DllImportSearchPath SherpaImportSearchPath =
        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, IntPtr> LoadedCudaLibraries =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _resolverRegistered;
    private static string? _bundledRuntimeDirectory;
    private static string? _cudaRuntimeDirectory;

    internal static IReadOnlyList<string> CudaPreloadFileNames =>
        SherpaCudaRuntimeInstaller.CudnnRuntimeFileNames;

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

    public static void ConfigureBundledRuntime()
    {
        lock (Sync)
        {
            RegisterResolverUnsafe();
            _cudaRuntimeDirectory = null;
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
        var onnxRuntime = Path.Join(runtimeDirectory, SherpaOnnxRuntimeDependencyFileName);
        if (!File.Exists(candidate) || !File.Exists(onnxRuntime))
            throw new DllNotFoundException(CreateMissingRuntimeMessage(runtimeDirectory));

        try
        {
            if (_cudaRuntimeDirectory is not null)
            {
                // cuDNN 9 resolves its split component libraries by name. A packaged
                // Windows host does not reliably honor a PATH update for those loads.
                lock (Sync)
                    PreloadCudaDependencies(runtimeDirectory, NativeLibrary.Load, LoadedCudaLibraries);
            }

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

    internal static void PreloadCudaDependencies(
        string runtimeDirectory,
        Func<string, IntPtr> loadLibrary,
        IDictionary<string, IntPtr> loadedLibraries)
    {
        var resolvedRuntimeDirectory = Path.GetFullPath(runtimeDirectory);
        foreach (var fileName in CudaPreloadFileNames)
        {
            var libraryPath = Path.Join(resolvedRuntimeDirectory, fileName);
            if (!File.Exists(libraryPath))
            {
                throw new DllNotFoundException(
                    $"The sherpa-onnx CUDA runtime is missing {fileName} under '{resolvedRuntimeDirectory}'.");
            }

            if (loadedLibraries.ContainsKey(libraryPath))
                continue;

            try
            {
                var handle = loadLibrary(libraryPath);
                if (handle == IntPtr.Zero)
                    throw new DllNotFoundException($"The native loader returned an invalid handle for {fileName}.");

                loadedLibraries.Add(libraryPath, handle);
            }
            catch (Exception ex) when (
                ex is DllNotFoundException
                    or BadImageFormatException
                    or FileLoadException)
            {
                throw new DllNotFoundException(
                    $"Unable to preload {fileName} from the sherpa-onnx CUDA runtime. Loader error: {ex.Message}",
                    ex);
            }
        }
    }

    private static void PrependToPath(string runtimeDirectory)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry => string.Equals(entry, runtimeDirectory, StringComparison.OrdinalIgnoreCase)))
            return;

        Environment.SetEnvironmentVariable("PATH", runtimeDirectory + Path.PathSeparator + current);
    }
}
