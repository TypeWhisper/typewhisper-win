using System.Globalization;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

// A package owns its resources. Loading strings never loads a plugin assembly.
internal sealed class PackagePluginLocalization : IPluginLocalization
{
    private readonly Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _englishKeys = new(StringComparer.Ordinal);
    private readonly Func<string> _currentLanguage;

    internal PackagePluginLocalization(string packageDirectory, Func<string>? currentLanguage = null)
    {
        _currentLanguage = currentLanguage ?? (() => CultureInfo.CurrentUICulture.Name);
        var directory = Path.Combine(packageDirectory, "Localization");
        if (!Directory.Exists(directory)) return;
        string[] files;
        try { files = Directory.GetFiles(directory, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Plugin localization directory could not be read."); return; }
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var language = Path.GetFileNameWithoutExtension(file);
            // Filenames use language tags; no arbitrary files or fallback paths are followed.
            if (language.Length > 35 || language.Length == 0 || language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) continue;
            try
            {
                var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (strings is not null) _languages[language] = strings;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { System.Diagnostics.Trace.TraceWarning("Plugin localization could not be read: {0}", language); }
        }
        if (_languages.TryGetValue("en", out var english))
            foreach (var (key, value) in english)
                if (value is not null) _englishKeys.TryAdd(value, key);
    }

    public string CurrentLanguage => _currentLanguage();
    public IReadOnlyList<string> AvailableLanguages => _languages.Keys.Order(StringComparer.Ordinal).ToArray();

    public string GetString(string key)
    {
        var resourceKey = _englishKeys.GetValueOrDefault(key, key);
        CultureInfo selected;
        try { selected = CultureInfo.GetCultureInfo(CurrentLanguage); }
        catch (CultureNotFoundException) { selected = CultureInfo.InvariantCulture; }
        for (var culture = selected; culture != CultureInfo.InvariantCulture; culture = culture.Parent)
        {
            var translated = Lookup(culture.Name, key, resourceKey);
            if (translated is not null) return translated;
        }
        return Lookup("en", key, resourceKey) ?? key;
    }

    private string? Lookup(string language, string key, string resourceKey)
    {
        if (!_languages.TryGetValue(language, out var strings)) return null;
        // Exact keys win when a package mixes literal English keys and symbolic resource keys.
        return strings.GetValueOrDefault(key) ?? strings.GetValueOrDefault(resourceKey);
    }

    public string GetString(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, GetString(key), args);
}
