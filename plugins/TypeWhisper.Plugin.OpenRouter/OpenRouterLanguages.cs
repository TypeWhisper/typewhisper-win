using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed partial class OpenRouterPlugin
{
    // Conservative language set documented for OpenAI speech-to-text. Other upstreams
    // retain automatic detection until their model-specific language sets are known.
    private static readonly IReadOnlyList<string> OpenAiLanguages = Array.AsReadOnly(
        "af ar hy az be bs bg ca zh hr cs da nl en et fi fr gl de el he hi hu is id it ja kn kk ko lv lt mk ms mi mr ne no fa pl pt ro ru sr sk sl es sw sv tl ta th tr uk ur vi cy".Split(' '));

    private static IReadOnlyList<string> LanguagesFor(string? model) => model is
        "openai/whisper-large-v3-turbo" or "openai/whisper-large-v3" or "openai/whisper-1"
        or "openai/gpt-4o-mini-transcribe" or "openai/gpt-4o-transcribe" or "openai/gpt-transcribe"
        ? OpenAiLanguages : [];

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => LanguagesFor(_selectedTranscriptionModelId);

    private static PluginModelInfo WithLanguages(PluginModelInfo model) => model with
    {
        LanguageCodes = LanguagesFor(model.Id), LanguageCount = LanguagesFor(model.Id).Count
    };
}
