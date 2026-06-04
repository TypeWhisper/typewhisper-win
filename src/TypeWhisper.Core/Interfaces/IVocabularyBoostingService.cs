namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the vocabulary boosting service contract.
/// </summary>
public interface IVocabularyBoostingService
{
    /// <summary>
    /// Applies the configured transformation to the supplied input.
    /// </summary>
    string Apply(string rawText);
}
