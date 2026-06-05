namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the translation service contract.
/// </summary>
public interface ITranslationService
{
    /// <summary>
    /// Performs translate asynchronously.
    /// </summary>
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct = default);
    /// <summary>
    /// Returns whether model ready.
    /// </summary>
    bool IsModelReady(string sourceLang, string targetLang);
    /// <summary>
    /// Returns whether model loading.
    /// </summary>
    bool IsModelLoading(string sourceLang, string targetLang);
}
