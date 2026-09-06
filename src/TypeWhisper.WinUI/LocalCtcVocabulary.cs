using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed class LocalCtcVocabulary : IAsyncDisposable
{
    internal const string PluginId = "com.typewhisper.parakeet-ctc";
    internal static readonly Version HostVersion = new(1, 1, 0);
    internal event Action? Changed;
    internal bool Busy { get; private set; }
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeWhisper-WinUI-DevUserData", "PluginData", "com.typewhisper.parakeet-ctc");
    private readonly VocabularyDiagnosticLog _diagnostics;
    private readonly VocabularyHostServices _host;
    private readonly VocabularyPluginSession _session;
    internal bool Enabled => _session.Enabled;
    internal string? Error { get; private set; }

    internal LocalCtcVocabulary(string? dataDirectory = null, Func<IPluginHostServices, Task<IVocabularyPluginLease>>? load = null, Func<string>? packageDirectory = null)
    {
        dataDirectory ??= DataDirectory;
        _diagnostics = new(Path.Combine(dataDirectory, "ctc-diagnostics.jsonl"));
        _host = new(dataDirectory, _diagnostics.Write);
        _session = new(() => load is not null ? load(_host) : VocabularyPluginLease.LoadAsync(
            packageDirectory?.Invoke() ?? Path.Combine(AppContext.BaseDirectory, "Plugins", PluginId), _host, HostVersion));
    }
    internal void Trace(string message) => _diagnostics.Write(message);

    internal async Task<string?> SetEnabledAsync(bool enabled)
    {
        if (!await _settingsGate.WaitAsync(0)) return "A plugin operation is already in progress.";
        Busy = true; Changed?.Invoke();
        try
        {
            // Enablement is owned by the parent transcription plugin, never a separate preference.
            await Task.Run(() => _session.SetEnabledAsync(enabled));
            return Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _session.SetEnabledAsync(false);
            return Error = "Dictionary boosting unavailable: " + ex.GetType().Name + ". Check the local plugin package and model.";
        }
        finally { _settingsGate.Release(); Busy = false; Changed?.Invoke(); }
    }

    internal async Task<VocabularyOutcome> RefineAsync(Guid recording, string text, float[] audio,
        IReadOnlyList<VocabularyTokenTiming> timings, IReadOnlyList<TypeWhisper.Core.Models.DictionaryEntry> terms)
    {
        Trace($"{recording} host-start enabled={Enabled} samples={audio.Length} timings={timings.Count} terms={terms.Count}");
        if (timings.Count == 0 || terms.Count == 0 || audio.Length == 0)
            Trace($"{recording} pipeline-skipped reason={(timings.Count == 0 ? "no-token-timings" : terms.Count == 0 ? "no-terms" : "no-audio")}");
        try
        {
            var result = await _session.RefineAsync(recording, text, audio, 16000, timings, terms.Select(t => new VocabularyTermHint(t.Original, t.CtcMinSimilarity)).ToArray());
            Trace($"{recording} host-finish modified={result.Modified} error={result.Error ?? "none"}");
            return result;
        }
        // Disabling the optional add-on must not discard an already decoded dictation.
        catch (OperationCanceledException) { Trace($"{recording} cancelled"); return new(text, false); }
        catch (ObjectDisposedException) { Trace($"{recording} disposed"); return new(text, false); }
    }
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
