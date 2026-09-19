using TypeWhisper.PluginSDK;
using System.Globalization;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Speechmatics;

public sealed partial class SpeechmaticsPlugin
{
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages { get; } = Array.AsReadOnly(new[]
    {
        "ar", "ar_en", "ba", "eu", "be", "bn", "bg", "yue", "ca", "hr", "cs", "da", "nl",
        "en", "eo", "et", "fi", "fr", "gl", "de", "el", "he", "hi", "hu", "id", "ia", "ga",
        "it", "ja", "ko", "lv", "lt", "ms", "en_ms", "mt", "cmn", "cmn_en", "cmn_en_ms_ta",
        "mr", "mn", "no", "fa", "pl", "pt", "ro", "ru", "sk", "sl", "es", "sw", "sv", "tl",
        "ta", "en_ta", "th", "tr", "uk", "ur", "ug", "vi", "cy"
    });

    private PluginSettingChoice[] LanguageChoices() => SupportedLanguages.Select(code =>
        new PluginSettingChoice(code, LanguageName(code))).ToArray();

    private string LanguageName(string code)
    {
        if (code.Contains('_')) return code.Replace("_", " + ");
        try { var culture = CultureInfo.GetCultureInfo(code); return Connection.L(culture.EnglishName, culture.NativeName); }
        catch (CultureNotFoundException) { return code; }
    }
}
