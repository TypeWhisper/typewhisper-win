using System.Text.RegularExpressions;

namespace TypeWhisper.Windows.Services;

internal static class ParakeetTailHelper
{
    internal const string ParakeetModelId = "plugin:com.typewhisper.sherpa-onnx:parakeet-tdt-0.6b";
    internal const int TailGuardMilliseconds = 200;
    private const int SampleRate = 16000;

    /// <summary>
    /// Returns whether parakeet model.
    /// </summary>
    public static bool IsParakeetModel(string? activeModelId) =>
        string.Equals(activeModelId, ParakeetModelId, StringComparison.Ordinal);

    /// <summary>
    /// Creates an app triggerend tail guard.
    /// </summary>
    public static float[] AppendTailGuard(float[] samples)
    {
        var tailSamples = SampleRate * TailGuardMilliseconds / 1000;
        if (tailSamples <= 0) return samples;

        var guarded = new float[samples.Length + tailSamples];
        Array.Copy(samples, guarded, samples.Length);
        return guarded;
    }

    /// <summary>
    /// Selects result.
    /// </summary>
    public static ParakeetTranscriptionSelection SelectResult(
        string? activeModelId,
        string? fullDecodeText,
        IReadOnlyList<string> partialSegments)
    {
        var partialText = string.Join(" ", partialSegments.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        var fullText = fullDecodeText?.Trim() ?? "";
        var isParakeet = IsParakeetModel(activeModelId);

        if (!isParakeet)
        {
            if (!string.IsNullOrWhiteSpace(partialText))
                return new ParakeetTranscriptionSelection(partialText, "partials", false, partialText.Length, fullText.Length);

            return new ParakeetTranscriptionSelection(fullText, string.IsNullOrWhiteSpace(fullText) ? "empty" : "full_decode", false, partialText.Length, fullText.Length);
        }

        if (!string.IsNullOrWhiteSpace(fullText))
        {
            return new ParakeetTranscriptionSelection(
                fullText,
                "full_decode",
                AreDivergent(fullText, partialText),
                partialText.Length,
                fullText.Length);
        }

        if (!string.IsNullOrWhiteSpace(partialText))
        {
            return new ParakeetTranscriptionSelection(
                partialText,
                "fallback_partials_after_empty_full_decode",
                false,
                partialText.Length,
                fullText.Length);
        }

        return new ParakeetTranscriptionSelection("", "empty", false, 0, 0);
    }

    internal static bool AreDivergent(string fullText, string partialText)
    {
        var normalizedFull = Normalize(fullText);
        var normalizedPartial = Normalize(partialText);
        if (normalizedFull.Length == 0 || normalizedPartial.Length == 0)
            return false;

        if (string.Equals(normalizedFull, normalizedPartial, StringComparison.Ordinal))
            return false;

        if (normalizedFull.Contains(normalizedPartial, StringComparison.Ordinal) ||
            normalizedPartial.Contains(normalizedFull, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), "\\s+", " ");
}

internal sealed record ParakeetTranscriptionSelection(
    string Text,
    string Source,
    bool DivergedFromPartials,
    int PartialTextLength,
    int FullDecodeTextLength);
