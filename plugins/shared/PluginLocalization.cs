using System.Globalization;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Shared;

/// <summary>Resolves package resources while preserving bilingual strings on older hosts.</summary>
internal static class PluginLocalization
{
    /// <summary>Returns a resource translation, or the supplied English/German fallback.</summary>
    public static string Get(IPluginHostServices? host, string english, string german)
    {
        IPluginLocalization? localization = null;
        try { localization = host?.Localization; }
        catch (NotSupportedException) { /* Older and headless hosts may not offer resources. */ }
        var translated = localization?.GetString(english);
        if (translated is not null && translated != english) return translated;
        var language = localization?.CurrentLanguage ?? CultureInfo.CurrentUICulture.Name;
        return language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? german : english;
    }
}
