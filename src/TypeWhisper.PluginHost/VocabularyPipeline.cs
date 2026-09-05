using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

public sealed record VocabularyOutcome(string Text, bool Modified, string? Error = null);

// One request at a time. Cancellation drains an uncooperative native decoder;
// it never unloads a model while native inference is using it.
public sealed class VocabularyPipeline
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<VocabularyOutcome> RefineAsync(IVocabularyRescorerPlugin? plugin,
        Guid recordingId, string text, float[] audio, int sampleRate,
        IReadOnlyList<VocabularyTokenTiming> timings, IReadOnlyList<VocabularyTermHint> terms,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plugin is null || terms.Count == 0 || timings.Count == 0 || audio.Length == 0)
            return new(text, false);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!plugin.IsReady) return new(text, false);
            // Keep a separate trusted snapshot: a plugin can cast readonly views back
            // to their underlying mutable objects, including ReadOnlyMemory PCM.
            var trustedTerms = terms.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Text))
                .DistinctBy(t => t.Text, StringComparer.Ordinal).ToArray();
            if (trustedTerms.Length == 0) return new(text, false);
            var request = new VocabularyRescoreRequest(recordingId, text, audio.ToArray(), sampleRate,
                Array.AsReadOnly(timings.ToArray()), Array.AsReadOnly(trustedTerms.ToArray()));
            var result = await plugin.RescoreAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var trusted = request with { Terms = trustedTerms };
            var refined = VocabularyResultValidator.Apply(trusted, result);
            return new(refined, refined != text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Do not expose audio, transcript or plugin error text in diagnostics.
            return new(text, false, "Vocabulary refinement failed: " + ex.GetType().Name);
        }
        finally { _gate.Release(); }
    }
}
