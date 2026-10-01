using System.Globalization;
using System.Text.Json;

namespace TypeWhisper.Core;

/// <summary>An interface language the app can be shown in.</summary>
/// <param name="Code">Language code, such as "de" or "zh-Hans".</param>
/// <param name="Name">The language's own name, shown in the language picker.</param>
public sealed record InterfaceLanguage(string Code, string Name);

/// <summary>
/// Translates interface text. As in the macOS string catalog, the English text is the key, so text without a
/// translation is shown in English.
/// </summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string> _catalog = new Dictionary<string, string>();
    private static Dictionary<string, string> _english = [];

    /// <summary>The languages offered in Settings; they match the macOS app.</summary>
    public static IReadOnlyList<InterfaceLanguage> Languages { get; } =
    [
        new("en", "English"), new("de", "Deutsch"), new("ja", "日本語"), new("zh-Hans", "简体中文")
    ];

    /// <summary>The code of the language in use.</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Returns the saved language if it is offered; otherwise the offered language closest to <paramref name="system"/>.</summary>
    public static string Resolve(string? saved, CultureInfo system)
    {
        if (Languages.Any(language => language.Code == saved)) return saved!;
        return system.TwoLetterISOLanguageName switch { "de" => "de", "ja" => "ja", "zh" => "zh-Hans", _ => "en" };
    }

    /// <summary>
    /// Switches the language for all text requested from now on. Call it once at startup, before any interface text
    /// is created: text that already exists keeps its language.
    /// </summary>
    public static void Use(string language)
    {
        Language = Resolve(language, CultureInfo.InvariantCulture);
        _catalog = Language == "en" ? new Dictionary<string, string>() : Catalog(Language);
        _english = [];
        foreach (var entry in _catalog.OrderBy(entry => entry.Key, StringComparer.Ordinal)) _english.TryAdd(entry.Value, entry.Key);
        // Plugins choose their own translations from the UI culture.
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>Returns <paramref name="text"/> in the language in use, or unchanged if it has no translation.</summary>
    public static string T(string text) => _catalog.TryGetValue(text, out var translated) ? translated : text;

    /// <summary>Translates <paramref name="format"/> and fills its numbered placeholders, such as {0}.</summary>
    public static string T(string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(format), arguments);

    /// <summary>
    /// Returns <paramref name="text"/> in <paramref name="language"/>, whatever language is in use. For the few texts
    /// that must be readable in a language that was just chosen and applies after a restart.
    /// </summary>
    public static string In(string language, string text) =>
        language == Language ? T(text) : Catalog(language).TryGetValue(text, out var translated) ? translated : text;

    /// <summary>
    /// Returns the English text behind a translated <paramref name="text"/>, or the text itself. Only for code that
    /// has to recognize a label it did not create; text with filled placeholders is not recognized.
    /// </summary>
    public static string English(string text) => _english.TryGetValue(text, out var english) ? english : text;

    /// <summary>
    /// Returns <paramref name="text"/> unchanged and registers it for translation. Use it where an English text
    /// also serves as an identifier, and translate the value with <see cref="T(string)"/> where it is displayed.
    /// </summary>
    public static string Mark(string text) => text;

    /// <summary>Returns the translations shipped for <paramref name="language"/>, keyed by English text.</summary>
    public static IReadOnlyDictionary<string, string> Catalog(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Localization/" + language + ".json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
