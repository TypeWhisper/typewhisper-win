using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Provides plugin localization behavior.
/// </summary>
public sealed class PluginLocalization : IPluginLocalization
{
    private const string FallbackLanguage = "en";
    private const string LocalizationFolder = "Localization";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, Dictionary<string, string>> _strings = [];
    private readonly string _localizationDir;

    /// <summary>
    /// Gets the current language.
    /// </summary>
    public string CurrentLanguage { get; }
    /// <summary>
    /// Gets the available languages.
    /// </summary>
    public IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>
    /// Initializes a new instance of the PluginLocalization class.
    /// </summary>
    public PluginLocalization(string pluginDirectory, string? languageOverride = null)
    {
        _localizationDir = Path.Combine(pluginDirectory, LocalizationFolder);
        CurrentLanguage = languageOverride
            ?? Loc.Instance.CurrentLanguage;

        var available = new List<string>();

        if (Directory.Exists(_localizationDir))
        {
            foreach (var file in Directory.EnumerateFiles(_localizationDir, "*.json"))
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(lang))
                    continue;

                try
                {
                    var json = File.ReadAllText(file);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                    if (dict is not null)
                    {
                        _strings[lang] = dict;
                        available.Add(lang);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginLocalization] Failed to load {file}: {ex.Message}");
                }
            }
        }

        AvailableLanguages = available;
    }

    /// <summary>
    /// Returns the localized string for the requested key.
    /// </summary>
    public string GetString(string key)
    {
        // Try current language
        if (_strings.TryGetValue(CurrentLanguage, out var currentDict) &&
            currentDict.TryGetValue(key, out var value))
        {
            return value;
        }

        // Try fallback language
        if (CurrentLanguage != FallbackLanguage &&
            _strings.TryGetValue(FallbackLanguage, out var fallbackDict) &&
            fallbackDict.TryGetValue(key, out var fallbackValue))
        {
            return fallbackValue;
        }

        // Return key as-is
        return key;
    }

    /// <summary>
    /// Returns the localized string for the requested key.
    /// </summary>
    public string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
