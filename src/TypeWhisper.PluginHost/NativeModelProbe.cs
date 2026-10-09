using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>
/// Loads a model on the graphics card in a throwaway process before a plugin trusts its native CUDA runtime.
/// The NVIDIA Parakeet plugin starts the host executable with <see cref="Argument"/>, so a native crash in a
/// broken driver or runtime ends only this process and the plugin can report it or fall back to the CPU.
/// </summary>
public static class NativeModelProbe
{
    /// <summary>The first command-line argument that selects probe mode.</summary>
    public const string Argument = "--native-model-probe";
    private const string PluginId = "com.typewhisper.sherpa-onnx";
    internal const int ExitLoaded = 0;
    internal const int ExitLoadFailed = 1;
    internal const int ExitInvalidArguments = 2;

    /// <summary>Whether the process was started as a native model probe.</summary>
    public static bool IsProbeInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Argument;

    /// <summary>Loads the model with CUDA and returns 0 when it loaded; errors are written to standard error.</summary>
    public static int Run(string[] args, Version hostVersion) => RunAsync(args, hostVersion).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args, Version hostVersion)
    {
        TranscriptionWorkerServer.SuppressCrashDialogs();
        if (!TryParse(args, out var pluginDirectory, out var assetDirectory, out var modelId, out var runtimeDirectory))
        {
            Console.Error.WriteLine("Native model probe received invalid arguments.");
            return ExitInvalidArguments;
        }
        try
        {
            // The package stays loaded until the process ends, as in the transcription worker.
            var services = new TranscriptionWorkerHostServices(assetDirectory, assetDirectory, (level, message) =>
            {
                if (level >= PluginLogLevel.Warning) Console.Error.WriteLine(message);
            });
            var package = await PortablePluginPackage.LoadAsync(pluginDirectory, services, hostVersion).ConfigureAwait(false);
            if (package.Plugin.PluginId != PluginId || package.Plugin is not ITranscriptionEnginePlugin engine)
            {
                Console.Error.WriteLine("Native model probe could not load the expected transcription plugin.");
                return ExitInvalidArguments;
            }
            engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
            await engine.LoadModelAsync(modelId, CancellationToken.None).ConfigureAwait(false);
            return ExitLoaded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return ExitLoadFailed;
        }
    }

    // Arguments after the first come in name/value pairs. The runtime must lie in the plugin's own CUDA folder.
    internal static bool TryParse(IReadOnlyList<string> args, out string pluginDirectory, out string assetDirectory,
        out string modelId, out string runtimeDirectory)
    {
        pluginDirectory = assetDirectory = modelId = runtimeDirectory = "";
        if (!IsProbeInvocation(args) || args.Count % 2 != 1) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
            if (!values.TryAdd(args[index], args[index + 1])) return false;
        if (!TryDirectory(values, "--plugin-directory", out pluginDirectory)
            || !TryDirectory(values, "--plugin-asset-directory", out assetDirectory)
            || !TryDirectory(values, "--runtime-directory", out runtimeDirectory)
            || !values.TryGetValue("--model-id", out var model) || string.IsNullOrWhiteSpace(model))
            return false;
        modelId = model;
        var runtimes = Path.Join(assetDirectory, "Runtimes", "sherpa-onnx-cuda") + Path.DirectorySeparatorChar;
        return runtimeDirectory.StartsWith(runtimes, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDirectory(Dictionary<string, string> values, string name, out string directory)
    {
        directory = "";
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) return false;
        try { directory = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return Directory.Exists(directory);
    }
}
