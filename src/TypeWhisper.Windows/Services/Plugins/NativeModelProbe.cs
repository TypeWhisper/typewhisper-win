using System.IO;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services.Plugins;

internal static class NativeModelProbe
{
    private const string ProbeArgument = "--native-model-probe";
    private const string PluginDirectoryArgument = "--plugin-directory";
    private const string PluginAssetDirectoryArgument = "--plugin-asset-directory";
    private const string ModelIdArgument = "--model-id";
    private const string RuntimeDirectoryArgument = "--runtime-directory";
    private const string ExpectedPluginId = "com.typewhisper.sherpa-onnx";

    internal static bool TryRun(string[] args, out int exitCode)
    {
        if (!args.Contains(ProbeArgument, StringComparer.OrdinalIgnoreCase))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunAsync(args).GetAwaiter().GetResult();
        return true;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var pluginDirectory = GetArgumentValue(args, PluginDirectoryArgument);
        var pluginAssetDirectory = GetArgumentValue(args, PluginAssetDirectoryArgument);
        var modelId = GetArgumentValue(args, ModelIdArgument);
        var runtimeDirectory = GetArgumentValue(args, RuntimeDirectoryArgument);

        if (!TryResolveDirectory(pluginDirectory, out var resolvedPluginDirectory)
            || !TryResolveDirectory(pluginAssetDirectory, out var resolvedPluginAssetDirectory)
            || !TryResolveDirectory(runtimeDirectory, out var resolvedRuntimeDirectory)
            || string.IsNullOrWhiteSpace(modelId))
        {
            Console.Error.WriteLine("Native model probe received invalid arguments.");
            return 2;
        }

        var expectedRuntimeRoot = Path.Join(
            resolvedPluginAssetDirectory,
            "Runtimes",
            "sherpa-onnx-cuda");
        if (!IsPathWithin(resolvedRuntimeDirectory, expectedRuntimeRoot))
        {
            Console.Error.WriteLine("Native model probe runtime directory is outside the plugin asset directory.");
            return 2;
        }

        LoadedPlugin? loaded = null;
        try
        {
            loaded = new PluginLoader().LoadPlugin(resolvedPluginDirectory);
            if (loaded is null
                || !string.Equals(loaded.Manifest.Id, ExpectedPluginId, StringComparison.OrdinalIgnoreCase)
                || loaded.Instance is not ITranscriptionEnginePlugin engine)
            {
                Console.Error.WriteLine("Native model probe could not load the expected transcription plugin.");
                return 2;
            }

            var host = new ProbePluginHostServices(resolvedPluginAssetDirectory);
            await loaded.Instance.ActivateAsync(host);
            engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
            await engine.LoadModelAsync(modelId, CancellationToken.None);
            await loaded.Instance.DeactivateAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (loaded is not null)
            {
                try
                {
                    loaded.Instance.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Probe cleanup failed: {ex.Message}");
                }

                loaded.LoadContext.Unload();
            }
        }
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static bool TryResolveDirectory(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            resolvedPath = Path.GetFullPath(path);
            return Directory.Exists(resolvedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsPathWithin(string path, string parentDirectory)
    {
        var resolvedParent = Path.GetFullPath(parentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(resolvedParent, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ProbePluginHostServices(string pluginAssetDirectory) : IPluginHostServices
    {
        public string PluginDataDirectory => pluginAssetDirectory;
        public string PluginAssetDirectory => pluginAssetDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new ProbePluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new ProbePluginLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value)
        {
        }

        public void Log(PluginLogLevel level, string message)
        {
        }

        public void NotifyCapabilitiesChanged()
        {
        }
    }

    private sealed class ProbePluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent
        {
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new ProbeDisposable();
    }

    private sealed class ProbePluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class ProbeDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
