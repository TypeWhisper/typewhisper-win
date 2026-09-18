namespace TypeWhisper.Plugin.Gemini;

public sealed partial class GeminiPlugin
{
    // Same ISO-to-BCP-47 defaults as the macOS provider at ac00e39e.
    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ar-EG",
        ["cs"] = "cs-CZ",
        ["da"] = "da-DK",
        ["de"] = "de-DE",
        ["el"] = "el-GR",
        ["en"] = "en-US",
        ["es"] = "es-419",
        ["fi"] = "fi-FI",
        ["fr"] = "fr-FR",
        ["he"] = "he-IL",
        ["hi"] = "hi-IN",
        ["hu"] = "hu-HU",
        ["id"] = "id-ID",
        ["it"] = "it-IT",
        ["ja"] = "ja-JP",
        ["ko"] = "ko-KR",
        ["nl"] = "nl-NL",
        ["no"] = "nb-NO",
        ["pl"] = "pl-PL",
        ["pt"] = "pt-BR",
        ["ro"] = "ro-RO",
        ["ru"] = "ru-RU",
        ["sv"] = "sv-SE",
        ["th"] = "th-TH",
        ["tr"] = "tr-TR",
        ["uk"] = "uk-UA",
        ["vi"] = "vi-VN",
        ["zh"] = "cmn-Hans-CN",
    };
}
