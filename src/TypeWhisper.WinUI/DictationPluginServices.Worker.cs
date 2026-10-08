using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal sealed partial class DictationPluginServices
{
    // Local engines whose native code runs in a worker process, so a crash there cannot end the app.
    private static readonly IReadOnlySet<string> IsolatedTranscriptionPlugins = new HashSet<string>(StringComparer.Ordinal)
    {
        LocalTranscriptionPlugin.PluginId, "com.typewhisper.whisper-cpp", "com.typewhisper.qwen3-local"
    };

    // The worker is this executable in worker mode. TYPEWHISPER_TRANSCRIPTION_WORKER=0 keeps the
    // engines in the app process for diagnosis.
    private TranscriptionIsolation? CreateTranscriptionIsolation()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "TypeWhisper.exe");
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIPTION_WORKER") == "0" || !File.Exists(executable)) return null;
        var isolation = new TranscriptionIsolation(executable, [TranscriptionWorkerServer.Argument], IsolatedTranscriptionPlugins, LocalCtcVocabulary.HostVersion)
            { IdleUnloadPolicy = IdlePolicy, RequestTimeout = WorkerRequestTimeout() };
        isolation.Notice += (_, message) => EngineNotice?.Invoke(message);
        return isolation;
    }

    private static TimeSpan? WorkerRequestTimeout()
    {
        var value = Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIPTION_TIMEOUT_SECONDS");
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value == "-1") return Timeout.InfiniteTimeSpan;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds is > 0 and <= 86400) return TimeSpan.FromSeconds(seconds);
        AppDiagnostics.Write("worker.timeout.invalid-setting");
        return null;
    }
}
