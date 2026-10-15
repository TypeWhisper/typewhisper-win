using System.Globalization;
using System.Text.Json;
using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Script;
internal static class PortableLocalization
{
    private static readonly IPluginLocalization Fallback = new PackageLocalization();
    internal static IPluginLocalization TryGet(IPluginHostServices? host)
    {
        try { return host?.Localization ?? Fallback; }
        catch (NotSupportedException) { return Fallback; }
    }

    private sealed class PackageLocalization : IPluginLocalization
    {
        private readonly Dictionary<string, Dictionary<string, string>> _strings = Load();
        public string CurrentLanguage => CultureInfo.CurrentUICulture.Name;
        public IReadOnlyList<string> AvailableLanguages => _strings.Keys.ToArray();
        public string GetString(string key)
        {
            var english = _strings.GetValueOrDefault("en");
            var resourceKey = english?.FirstOrDefault(p => p.Value == key).Key ?? key;
            for (var culture = CultureInfo.CurrentUICulture; culture.Name.Length > 0; culture = culture.Parent)
            {
                var language = culture.Name switch { "zh-Hant" => "zh-TW", "zh" => "zh-Hans", _ => culture.Name };
                if (_strings.GetValueOrDefault(language)?.GetValueOrDefault(resourceKey) is { } translated)
                    return translated;
                // Traditional Chinese must fall back to English when its resource is unavailable.
                if (culture.Name == "zh-Hant") break;
            }
            return english?.GetValueOrDefault(resourceKey) ?? key;
        }
        public string GetString(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, GetString(key), args);
        private static Dictionary<string, Dictionary<string, string>> Load()
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var directory = Path.Combine(Path.GetDirectoryName(typeof(ScriptPlugin).Assembly.Location)!, "Localization");
            foreach (var language in new[] { "en", "de", "ja", "ru", "zh-Hans", "zh-TW" })
            {
                try { result[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, language + ".json"))) ?? []; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            return result;
        }
    }
}
