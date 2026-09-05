using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed class LocalCtcVocabulary : IAsyncDisposable
{
    private static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeWhisper-WinUI-DevUserData", "PluginData", "com.typewhisper.parakeet-ctc");
    private readonly VocabularyDiagnosticLog _diagnostics = new(Path.Combine(DataDirectory, "ctc-diagnostics.jsonl"));
    private readonly VocabularyHostServices _host;
    private readonly VocabularyPluginSession _session;
    internal bool Enabled => _session.Enabled;
    internal string? Error { get; private set; }

    internal LocalCtcVocabulary()
    {
        _host = new(DataDirectory, _diagnostics.Write);
        _session = new(() => VocabularyPluginLease.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "Plugins", "com.typewhisper.parakeet-ctc"), _host, new Version(0, 0, 1)));
    }
    internal void Trace(string message) => _diagnostics.Write(message);

    internal async Task InitializeAsync()
    {
        try { if (_host.GetSetting<bool>("Enabled")) await _session.SetEnabledAsync(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = "CTC unavailable: " + ex.GetType().Name; }
        Trace($"initialize enabled={Enabled} error={Error ?? "none"}");
    }

    internal async Task<string?> SetEnabledAsync(bool enabled)
    {
        try
        {
            // Load first. A missing/invalid model must not persist a successful enable.
            await _session.SetEnabledAsync(enabled);
            _host.SetSetting("Enabled", enabled);
            return Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _session.SetEnabledAsync(false);
            return Error = "Could not enable/save CTC: " + ex.GetType().Name + ". Check the local plugin package and model.";
        }
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
