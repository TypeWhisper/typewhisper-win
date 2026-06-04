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

    private readonly Dictionary<string, Dictionary<string, string>> _strings = [];
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

    private Loc() { }

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
            if (_currentLanguage == value) return;
            _currentLanguage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
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

        AvailableLanguages = available;
        AvailableUiLanguages = BuildUiLanguageOptions(available);
    }

    private static IReadOnlyList<UiLanguageOption> BuildUiLanguageOptions(List<string> codes)
    {
        var displayNames = new Dictionary<string, string>
        {
            ["en"] = "English",
            ["de"] = "Deutsch",
            ["ja"] = "日本語",
            ["ru"] = "Русский",
        };

        var options = new List<UiLanguageOption>
        {
            new(null, "Auto (System)")
        };

        foreach (var code in codes.OrderBy(c => c))
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
            code = culture.TwoLetterISOLanguageName;
            Debug.WriteLine($"[Loc] GetUserDefaultUILanguage() LANGID=0x{langId:X4} -> \"{culture.Name}\" -> \"{code}\"");
        }
        catch (Exception ex)
        {
            code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Debug.WriteLine($"[Loc] GetUserDefaultUILanguage() failed ({ex.Message}), fallback CurrentUICulture=\"{code}\"");
        }

        var result = HasLanguage(code) ? code : FallbackLanguage;
        Debug.WriteLine($"[Loc] DetectSystemLanguage() -> \"{result}\"");
        return result;
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
