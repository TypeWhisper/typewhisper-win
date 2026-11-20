using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Zoom;

// Catalog and request rules ported from the macOS ZoomPlugin.swift (Zoom Scribe 1.0.0, TypeWhisper/typewhisper-mac#1978).
/// <summary>
/// A Scribe model as the plugin offers it. <c>Languages</c> are the two-letter codes of the dictation languages it takes;
/// <c>UsesVocabulary</c> says whether it sends <c>vocabulary.phrases</c>. Zoom's multilingual model accepts the list but
/// applies none of it (a live session reports <c>phrase_count: 0</c>), so it sends none.
/// </summary>
internal sealed record ZoomModel(string Id, string Name, IReadOnlyList<string> Languages, bool UsesVocabulary);

/// <summary>How dictations are transcribed.</summary>
internal static class ZoomModes
{
    /// <summary>Dictations stream to Scribe's Live mode, which also writes the final text. Recordings and files use the Fast mode.</summary>
    internal const string Live = "live";
    /// <summary>Every recording goes to the Fast mode after it stops; no live preview from Zoom.</summary>
    internal const string Batch = "batch";
    internal static readonly IReadOnlyList<string> All = [Live, Batch];
    internal static string Normalize(string? mode) => All.Contains(mode) ? mode! : Live;
}

internal static class ZoomModels
{
    internal const string MultilingualId = "zoom-scribe-multilingual";
    /// <summary>Not a Zoom model ID: requests go to <c>zoom-scribe-&lt;language&gt;</c> for the dictation language.</summary>
    internal const string LanguageId = "zoom-scribe-language";
    internal const string EnglishProId = "zoom-scribe-en-pro";

    /// <summary>
    /// One model for ten languages, with language detection. It had the lowest error rate in Spanish, French and Italian of
    /// all models in TypeWhisper's benchmark (TypeWhisper/typewhisper-benchmark#29).
    /// </summary>
    internal static readonly ZoomModel Multilingual = new(MultilingualId, "Scribe Multilingual",
        Array.AsReadOnly(new[] { "en", "de", "es", "fr", "it", "pt", "ja", "ko", "zh", "ar" }), UsesVocabulary: false);
    /// <summary>
    /// Zoom's model for the dictation language (<c>zoom-scribe-de</c> and so on), which applies dictionary terms. Without a
    /// dictation language, or for Korean, Scribe Multilingual takes the request.
    /// </summary>
    internal static readonly ZoomModel Language = new(LanguageId, "Scribe Single Language",
        Array.AsReadOnly(new[] { "en", "de", "es", "fr", "it", "pt", "ja", "zh", "ar" }), UsesVocabulary: true);
    /// <summary>Zoom's premium English model.</summary>
    internal static readonly ZoomModel EnglishPro = new(EnglishProId, "Scribe English Pro", Array.AsReadOnly(new[] { "en" }), UsesVocabulary: true);

    internal static readonly IReadOnlyList<ZoomModel> All = Array.AsReadOnly(new[] { Multilingual, Language, EnglishPro });
    internal static ZoomModel Default => Multilingual;

    /// <summary>Zoom takes at most 100 inline vocabulary phrases.</summary>
    internal static readonly DictionaryTermsBudget Budget = new(MaxTerms: 100, MaxCharsPerTerm: 100);

    /// <summary>The locale Scribe expects for a two-letter language code.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Locales = new Dictionary<string, string>
    {
        ["en"] = "en-US", ["de"] = "de-DE", ["es"] = "es-ES", ["fr"] = "fr-FR", ["it"] = "it-IT",
        ["pt"] = "pt-BR", ["ja"] = "ja-JP", ["ko"] = "ko-KR", ["zh"] = "zh-CN", ["ar"] = "ar-SA"
    };

    /// <summary>The model for a saved ID; unknown IDs get the default.</summary>
    internal static ZoomModel Resolve(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Default;

    /// <summary>The model for an explicit selection; unknown IDs are rejected.</summary>
    internal static ZoomModel Require(string id) =>
        All.FirstOrDefault(m => m.Id == id) ?? throw new ArgumentException("Unknown Zoom Scribe model.", nameof(id));

    /// <summary>The two-letter code of a language such as <c>de</c>, <c>de-DE</c> or <c>pt_BR</c>; null for none or <c>auto</c>.</summary>
    internal static string? LanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var code = language.Trim().Split('-', '_')[0].ToLowerInvariant();
        return code is "" or "auto" ? null : code;
    }

    internal static IReadOnlyList<string> Terms(string? prompt) =>
        PluginDictionaryTerms.Clip(PluginDictionaryTerms.ParsePrompt(prompt, [',', ';', '\n', '\r']), Budget);
}

/// <summary>The settings of one Fast request or Live session.</summary>
/// <param name="Model">Scribe's <c>config.model</c>.</param>
/// <param name="Locale">Scribe's <c>config.language</c>, or null when Scribe detects the language.</param>
/// <param name="Vocabulary">Scribe's <c>config.vocabulary.phrases</c>.</param>
internal sealed record ZoomRequest(string Model, string? Locale, IReadOnlyList<string> Vocabulary)
{
    /// <summary>The two-letter code of <see cref="Locale"/>.</summary>
    internal string? LanguageCode => ZoomModels.LanguageCode(Locale);

    /// <summary>
    /// The model and language for a request. A language the model does not take, or none, goes to the multilingual model,
    /// with that language when it takes it and with language detection otherwise.
    /// </summary>
    internal static ZoomRequest For(ZoomModel model, string? language, string? prompt)
    {
        var code = ZoomModels.LanguageCode(language);
        if (code is not null && model.Languages.Contains(code) && ZoomModels.Locales.TryGetValue(code, out var locale))
        {
            var id = model.Id == ZoomModels.LanguageId ? "zoom-scribe-" + code : model.Id;
            return new(id, locale, model.UsesVocabulary ? ZoomModels.Terms(prompt) : []);
        }
        var multilingual = ZoomModels.Multilingual;
        if (code is not null && multilingual.Languages.Contains(code) && ZoomModels.Locales.TryGetValue(code, out locale))
            return new(multilingual.Id, locale, []);
        return new(multilingual.Id, null, []);
    }

    /// <summary>Scribe's <c>config</c> object; <paramref name="interimResults"/> asks a live session for partial text.</summary>
    internal Dictionary<string, object> Config(bool interimResults = false)
    {
        var config = new Dictionary<string, object> { ["model"] = Model };
        if (Locale is not null) config["language"] = Locale;
        // Continuous detection works with the multilingual model in both modes; "once" picked English for German speech
        // in the macOS tests.
        else config["language_detection"] = new Dictionary<string, object> { ["mode"] = "continuous" };
        if (Vocabulary.Count > 0) config["vocabulary"] = new Dictionary<string, object> { ["phrases"] = Vocabulary };
        if (interimResults) config["interim_results"] = true;
        return config;
    }
}
