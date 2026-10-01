namespace TypeWhisper.Core.Models;

/// <summary>Model identifiers of the local NVIDIA Parakeet plugin.</summary>
public static class ParakeetModels
{
    /// <summary>
    /// Whether the model is a Parakeet TDT 0.6B v3 variant. They share one tokenizer, so token timings
    /// and formatting behave alike.
    /// </summary>
    public static bool IsParakeetTdt(string? modelId) =>
        string.Equals(modelId, "parakeet-tdt-0.6b", StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, "parakeet-ultra-0.6b", StringComparison.OrdinalIgnoreCase);
}
