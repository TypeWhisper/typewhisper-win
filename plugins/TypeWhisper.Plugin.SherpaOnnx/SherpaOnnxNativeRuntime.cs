using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal static class SherpaOnnxNativeRuntime
{
    private static readonly object Sync = new();
    private static bool _resolverRegistered;
    private static string? _cudaRuntimeDirectory;

    public static void RegisterResolver()
    {
        lock (Sync)
            RegisterResolverUnsafe();
    }

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
        var runtimeDirectory = _cudaRuntimeDirectory;
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            return IntPtr.Zero;

        var fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : libraryName + ".dll";
        var candidate = Path.Combine(runtimeDirectory, fileName);

        return File.Exists(candidate)
            ? NativeLibrary.Load(candidate)
            : IntPtr.Zero;
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
