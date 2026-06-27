namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents a dictionary correction learned automatically from an observed edit.
/// </summary>
/// <param name="Id">Dictionary entry id that was added.</param>
/// <param name="Original">Original token that was corrected.</param>
/// <param name="Replacement">Replacement token learned for the original token.</param>
public sealed record LearnedDictionaryCorrection(string Id, string Original, string Replacement);
