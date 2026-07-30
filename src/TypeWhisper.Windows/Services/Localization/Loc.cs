using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services.Localization;

/// <summary>
/// Represents ui language option data.
/// </summary>
/// <param name="Code">Code supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record UiLanguageOption(string? Code, string DisplayName);

/// <summary>
/// Singleton localization service for the UI.
/// Loads JSON translation files from Resources/Localization/{lang}.json.
/// Fallback chain: selected language -> "en" -> key itself.
/// Fires PropertyChanged("Item[]") on language change so all WPF bindings update.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private const string FallbackLanguage = "en";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _strings =
        new(StringComparer.OrdinalIgnoreCase);
    private string _currentLanguage = FallbackLanguage;
    private string? _localizationDir;

    /// <summary>
    /// Raised when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>
    /// Raised when language changes.
    /// </summary>
    public event EventHandler? LanguageChanged;

    private Loc() => Initialize();

    /// <summary>
    /// Indexer used by StrExtension bindings: Loc.Instance["Key"]
    /// </summary>
    public string this[string key] => GetString(key);

    /// <summary>
    /// Gets the current language.
    /// </summary>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            var resolved = ResolveLanguage(value, AvailableLanguages);
            if (string.Equals(_currentLanguage, resolved, StringComparison.OrdinalIgnoreCase)) return;
            _currentLanguage = resolved;
            AvailableUiLanguages = BuildUiLanguageOptions(AvailableLanguages);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableUiLanguages)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets the available languages value.
    /// </summary>
    public IReadOnlyList<string> AvailableLanguages { get; private set; } = [];

    /// <summary>
    /// Gets or sets the available ui languages value.
    /// </summary>
    public IReadOnlyList<UiLanguageOption> AvailableUiLanguages { get; private set; } = [];

    /// <summary>
    /// Initializes resources required before use.
    /// </summary>
    public void Initialize()
    {
        var baseDir = AppContext.BaseDirectory;
        _localizationDir = Path.Combine(baseDir, "Resources", "Localization");
        _strings.Clear();

        var available = new List<string>();

        if (Directory.Exists(_localizationDir))
        {
            foreach (var file in Directory.EnumerateFiles(_localizationDir, "*.json"))
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(lang)) continue;

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
                    Debug.WriteLine($"[Loc] Failed to load {file}: {ex.Message}");
                }
            }
        }

        AvailableLanguages = available
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _currentLanguage = ResolveLanguage(_currentLanguage, AvailableLanguages);
        AvailableUiLanguages = BuildUiLanguageOptions(AvailableLanguages);
    }

    private IReadOnlyList<UiLanguageOption> BuildUiLanguageOptions(IReadOnlyList<string> codes)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["de"] = "Deutsch",
            ["ja"] = "日本語",
            ["ru"] = "Русский",
            ["zh-Hans"] = "简体中文",
            ["zh-Hant"] = "繁體中文",
        };

        var options = new List<UiLanguageOption>
        {
            new(null, GetString("General.LanguageAuto"))
        };

        foreach (var code in codes)
        {
            var display = displayNames.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
            options.Add(new(code, display));
        }

        return options;
    }

    /// <summary>
    /// Returns whether language.
    /// </summary>
    public bool HasLanguage(string langCode) => _strings.ContainsKey(langCode);

    /// <summary>
    /// Auto-detect language from the Windows user default UI language, falling back to
    /// CultureInfo.CurrentUICulture, then to English if the detected language is unavailable.
    /// Uses GetUserDefaultUILanguage() which reads the registry directly and is not affected
    /// by parent-process culture inheritance (e.g. Velopack installer).
    /// </summary>
    public string DetectSystemLanguage()
    {
        string code;
        try
        {
            var langId = NativeMethods.GetUserDefaultUILanguage();
            var culture = CultureInfo.GetCultureInfo(langId);
            code = culture.Name;
            Debug.WriteLine($"[Loc] GetUserDefaultUILanguage() LANGID=0x{langId:X4} -> \"{culture.Name}\" -> \"{code}\"");
        }
        catch (Exception ex)
        {
            code = CultureInfo.CurrentUICulture.Name;
            Debug.WriteLine($"[Loc] GetUserDefaultUILanguage() failed ({ex.Message}), fallback CurrentUICulture=\"{code}\"");
        }

        var result = ResolveLanguage(code, AvailableLanguages);
        Debug.WriteLine($"[Loc] DetectSystemLanguage() -> \"{result}\"");
        return result;
    }

    /// <summary>
    /// Resolves a culture name to an available localization resource.
    /// Chinese regions are mapped to their standard script tags so adding a
    /// zh-Hant resource automatically enables Traditional Chinese regions.
    /// </summary>
    internal static string ResolveLanguage(string? cultureName, IEnumerable<string> availableLanguages)
    {
        var available = availableLanguages
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in LanguageCandidates(cultureName))
        {
            var match = available.FirstOrDefault(code =>
                string.Equals(code, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return available.FirstOrDefault(code =>
            string.Equals(code, FallbackLanguage, StringComparison.OrdinalIgnoreCase))
            ?? FallbackLanguage;
    }

    private static IEnumerable<string> LanguageCandidates(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            yield break;

        var normalized = cultureName.Trim().Replace('_', '-');
        yield return normalized;

        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            yield break;

        var primary = parts[0];
        if (!string.Equals(primary, "zh", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 1)
                yield return primary;
            yield break;
        }

        var script = parts.FirstOrDefault(part =>
            part.Equals("Hans", StringComparison.OrdinalIgnoreCase)
            || part.Equals("Hant", StringComparison.OrdinalIgnoreCase));
        if (script is null)
        {
            if (parts.Any(part => part.Equals("TW", StringComparison.OrdinalIgnoreCase)
                || part.Equals("HK", StringComparison.OrdinalIgnoreCase)
                || part.Equals("MO", StringComparison.OrdinalIgnoreCase)
                || part.Equals("CHT", StringComparison.OrdinalIgnoreCase)))
            {
                script = "Hant";
            }
            else if (parts.Length == 1 || parts.Any(part =>
                part.Equals("CN", StringComparison.OrdinalIgnoreCase)
                || part.Equals("SG", StringComparison.OrdinalIgnoreCase)
                || part.Equals("MY", StringComparison.OrdinalIgnoreCase)
                || part.Equals("CHS", StringComparison.OrdinalIgnoreCase)))
            {
                script = "Hans";
            }
        }

        if (script is not null)
            yield return $"zh-{script}";

        if (parts.Length > 1)
            yield return primary;
    }

    /// <summary>
    /// Returns the localized string for the requested key.
    /// </summary>
    public string GetString(string key)
    {
        if (_strings.TryGetValue(_currentLanguage, out var currentDict) &&
            currentDict.TryGetValue(key, out var value))
            return value;

        if (_currentLanguage != FallbackLanguage &&
            _strings.TryGetValue(FallbackLanguage, out var fallbackDict) &&
            fallbackDict.TryGetValue(key, out var fallbackValue))
            return fallbackValue;

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
