using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>Builds valid token intervals from local decoder timing metadata.</summary>
public static class TranscriptionTokenTimings
{
    // Transducers may emit several pieces at the same frame. Preserve every
    // piece and share the interval up to the next strictly later timestamp.
    /// <summary>Preserves shared-frame tokens and rejects invalid timing arrays.</summary>
    public static VocabularyTokenTiming[] Create(string[] tokens, float[] starts, float[]? durations, double audioSeconds)
    {
        if (tokens.Length != starts.Length || !double.IsFinite(audioSeconds) || audioSeconds <= 0) return [];
        var result = new List<VocabularyTokenTiming>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var start = starts[i];
            if (!float.IsFinite(start) || start < 0 || start >= audioSeconds || (i > 0 && start < starts[i - 1])) return [];
            var end = (double)start;
            if (durations?.Length == tokens.Length && float.IsFinite(durations[i]) && durations[i] > 0)
                end = Math.Min(audioSeconds, (double)start + durations[i]);
            // Check the computed interval, not only duration > 0: tiny positive
            // durations can round away even in double precision. Never pass a
            // zero-length interval to CTC or silently drop the token.
            if (!(end > start))
            {
                end = audioSeconds;
                for (var next = i + 1; next < starts.Length; next++)
                    if (starts[next] > start) { end = Math.Min(audioSeconds, starts[next]); break; }
            }
            result.Add(new(tokens[i], start, end));
        }
        return result.ToArray();
    }
}
