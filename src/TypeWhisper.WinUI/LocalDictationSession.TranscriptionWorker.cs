using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // Local engines whose native code runs in a worker process, so a crash there cannot end the app.
    private static readonly IReadOnlySet<string> IsolatedTranscriptionPlugins = new HashSet<string>(StringComparer.Ordinal)
    {
        LocalTranscriptionPlugin.PluginId, "com.typewhisper.whisper-cpp", "com.typewhisper.qwen3-local"
    };

    /// <summary>Raised off the UI thread when a local engine recovered from crashes by changing how it runs.</summary>
    internal event Action<string>? EngineNotice;

    // The worker is this executable in worker mode. TYPEWHISPER_TRANSCRIPTION_WORKER=0 keeps the
    // engines in the app process for diagnosis.
    private TranscriptionIsolation? CreateTranscriptionIsolation()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "TypeWhisper.exe");
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIPTION_WORKER") == "0" || !File.Exists(executable)) return null;
        var isolation = new TranscriptionIsolation(executable, [TranscriptionWorkerServer.Argument], IsolatedTranscriptionPlugins, LocalCtcVocabulary.HostVersion)
            { IdleUnloadPolicy = ModelIdlePolicy };
        isolation.Notice += (_, message) => EngineNotice?.Invoke(message);
        return isolation;
    }
}
