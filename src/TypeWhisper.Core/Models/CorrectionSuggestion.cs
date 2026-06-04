namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents correction suggestion data.
/// </summary>
/// <param name="Original">Original supplied to the member.</param>
/// <param name="Replacement">Replacement supplied to the member.</param>
public sealed record CorrectionSuggestion(string Original, string Replacement);
