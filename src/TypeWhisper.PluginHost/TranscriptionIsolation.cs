using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>
/// Decides which local engines run in a worker process and creates their host-side adapters.
/// The worker executable is started with <paramref name="prefixArguments"/> before its own arguments.
/// </summary>
public sealed class TranscriptionIsolation(string executablePath, IReadOnlyList<string> prefixArguments,
    IReadOnlySet<string> pluginIds, Version hostVersion)
{
    private const long MaximumLogBytes = 512 * 1024;
    private static readonly object LogSync = new();

    /// <summary>Raised with the plugin id and a user-facing message; subscribers must dispatch to their UI thread.</summary>
    public event Action<string, string>? Notice;
    /// <summary>Ends idle workers after this policy's delay; null keeps workers running.</summary>
    public ModelIdleUnloadPolicy? IdleUnloadPolicy { get; init; }

    /// <summary>Returns an isolated adapter, or null when the engine stays in process.</summary>
    public IsolatedTranscriptionEngine? TryIsolate(ITranscriptionEnginePlugin engine, string packageDirectory, IPluginHostServices services)
    {
        // Streaming sessions are callback objects that cannot cross the process boundary.
        if (!pluginIds.Contains(engine.PluginId) || engine is not IPcmTranscriptionEnginePlugin || engine.SupportsStreaming) return null;
        var launch = new TranscriptionWorkerLaunch(executablePath, prefixArguments, Path.GetFullPath(packageDirectory),
            services.PluginDataDirectory, services.PluginAssetDirectory, engine.GetTranscriptionSelectionId(), hostVersion);
        var logPath = Path.Combine(services.PluginDataDirectory, "transcription-worker.log");
        void Log(PluginLogLevel level, string message)
        {
            try { services.Log(level, message); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (level >= PluginLogLevel.Warning) AppendLog(logPath, level, message);
        }
        var isolated = new IsolatedTranscriptionEngine(engine,
            async (acceleration, ct) => await TranscriptionWorkerProcess.StartAsync(launch, acceleration, Log, ct).ConfigureAwait(false), Log,
            idlePolicy: IdleUnloadPolicy);
        isolated.Notice += message => Notice?.Invoke(engine.PluginId, message);
        return isolated;
    }

    // Keeps crash details on disk for support; the previous file is kept once when the log grows too large.
    private static void AppendLog(string path, PluginLogLevel level, string message)
    {
        lock (LogSync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
