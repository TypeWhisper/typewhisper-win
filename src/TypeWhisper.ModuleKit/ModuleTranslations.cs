using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TypeWhisper.ModuleKit;

/// <summary>
/// A module's own translations, keyed by English text like the host's. They live in the module, so module text never
/// enters the host's catalog. Text without a translation is shown in English.
/// </summary>
public sealed class ModuleTranslations
{
    private readonly IReadOnlyDictionary<string, string> _catalog;

    /// <summary>Uses <paramref name="catalog"/> as the translations for the language in use.</summary>
    public ModuleTranslations(IReadOnlyDictionary<string, string> catalog) => _catalog = catalog;

    /// <summary>
    /// Loads the embedded resource <c>&lt;prefix&gt;&lt;language&gt;.json</c> from <paramref name="assembly"/>, a JSON object
    /// from English text to translation. English, or a missing resource, gives no translations.
    /// </summary>
    public static ModuleTranslations Load(Assembly assembly, string language, string resourcePrefix = "Localization/")
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrEmpty(language) || language == "en") return new(new Dictionary<string, string>());
        using var stream = assembly.GetManifestResourceStream(resourcePrefix + language + ".json");
        if (stream is null) return new(new Dictionary<string, string>());
        return new(JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []);
    }

    /// <summary>Returns <paramref name="text"/> in the language in use, or unchanged if it has no translation.</summary>
    public string T(string text) => _catalog.TryGetValue(text, out var translated) ? translated : text;

    /// <summary>Translates <paramref name="format"/> and fills its numbered placeholders, such as {0}.</summary>
    public string T(string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(format), arguments);
}
