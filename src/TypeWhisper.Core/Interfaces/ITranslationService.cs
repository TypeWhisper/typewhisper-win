namespace TypeWhisper.Core.Interfaces;

public interface ITranslationService
{
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct = default);
    bool IsModelReady(string sourceLang, string targetLang);
    bool IsModelLoading(string sourceLang, string targetLang);
}
