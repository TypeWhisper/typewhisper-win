using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Owns the plugin graph and its release order. Dictation supplies only its idle-admission rule;
// capture/output orchestration does not construct workers, package registries or speech backends.
internal sealed partial class DictationPluginServices : IAsyncDisposable
{
    private readonly AsyncShutdownCoordinator _shutdown = new();
    internal WinUIPluginPackages Packages { get; } = new();
    internal ModelMemoryPreferencesStore ModelMemoryPreferences { get; } = new(WinUIProfile.DataPath("model-memory.json"));
    internal ModelIdleUnloadPolicy IdlePolicy { get; }
    internal LocalTranscriptionPlugin Models { get; }
    internal LocalCtcVocabulary Vocabulary { get; }
    internal PortablePluginRuntimeRegistry Runtime { get; }
    internal PluginSpokenFeedbackBackend SpeechBackend { get; }
    internal SpokenFeedbackController SpokenFeedback { get; }
    internal event Action? Changed;
    internal event Action<string>? EngineNotice;

    internal DictationPluginServices(Func<bool> canUnload)
    {
        IdlePolicy = new(ModelMemoryPreferences.AutoUnloadSeconds) { CanUnload = canUnload };
        var isolation = CreateTranscriptionIsolation();
        Models = new(packageDirectory: () => Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId), isolation: isolation);
        Vocabulary = new(packageDirectory: () => Path.Combine(Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId), "Dependencies", LocalCtcVocabulary.PluginId),
            idlePolicy: IdlePolicy);
        Vocabulary.LoadFailed += ex => AppDiagnostics.Write("dictation.vocabulary-load.failed", ex);
        Runtime = new(Packages.Store, LocalCtcVocabulary.HostVersion, WinUIPluginPackages.CreateServices,
            id => id is not (LocalTranscriptionPlugin.PluginId or LocalCtcVocabulary.PluginId))
            { TranscriptionIsolation = isolation, IdleUnloadPolicy = IdlePolicy };
        Runtime.Changed += () => Changed?.Invoke();
        SpeechBackend = new(Runtime, new WindowsSystemVoiceBackend());
        SpokenFeedback = new(SpeechBackend);
    }

    // The session drains capture, downloads and speech before releasing the providers.
    public ValueTask DisposeAsync() => new(_shutdown.Run(async () =>
    {
        var failures = new List<Exception>();
        foreach (var service in new IAsyncDisposable[] { Vocabulary, Runtime, Models })
        {
            try { await service.DisposeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { failures.Add(ex); AppDiagnostics.WriteFailure("plugins.shutdown.step-failed", ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Some plugin resources could not be shut down cleanly.", failures);
    }));
}
