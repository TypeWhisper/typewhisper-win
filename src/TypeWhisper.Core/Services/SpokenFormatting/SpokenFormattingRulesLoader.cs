using System.Reflection;
using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>
/// Loads and caches embedded spoken formatting rules.
/// </summary>
public sealed class SpokenFormattingRulesLoader
{
    private static readonly string[] Languages = ["de", "en"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Lazy<IReadOnlyDictionary<string, SpokenFormattingRuleSet>> CachedRuleSets =
        new(LoadRuleSets, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IReadOnlyDictionary<string, SpokenFormattingRuleSet> _ruleSets;

    /// <summary>Initializes a new instance and loads the embedded rule resources.</summary>
    public SpokenFormattingRulesLoader()
    {
        _ruleSets = CachedRuleSets.Value;
    }

    private static IReadOnlyDictionary<string, SpokenFormattingRuleSet> LoadRuleSets()
    {
        var assembly = typeof(SpokenFormattingRulesLoader).Assembly;
        return Languages
            .Select(language => LoadRuleSet(assembly, language))
            .Where(static ruleSet => ruleSet is not null)
            .ToDictionary(static ruleSet => ruleSet!.Language, static ruleSet => ruleSet!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the supported primary language codes.</summary>
    public IReadOnlyCollection<string> SupportedLanguages => _ruleSets.Keys.ToArray();

    /// <summary>Gets the cached rule set for a language.</summary>
    public SpokenFormattingRuleSet? RuleSetFor(string? languageCode)
    {
        var normalized = SpokenFormattingLanguageNormalizer.Normalize(languageCode);
        return normalized is not null && _ruleSets.TryGetValue(normalized, out var ruleSet)
            ? ruleSet
            : null;
    }

    /// <summary>Determines whether a language has a valid embedded rule set.</summary>
    public bool Supports(string? languageCode) => RuleSetFor(languageCode) is not null;

    private static SpokenFormattingRuleSet? LoadRuleSet(Assembly assembly, string language)
    {
        var resourceName = $"TypeWhisper.Core.Resources.SpokenFormatting.{language}.json";
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                return null;

            var ruleSet = JsonSerializer.Deserialize<SpokenFormattingRuleSet>(stream, JsonOptions);
            var normalizedLanguage = SpokenFormattingLanguageNormalizer.Normalize(ruleSet?.Language);
            if (ruleSet is null
                || normalizedLanguage != language
                || ruleSet.Rules.Count == 0
                || ruleSet.Rules.Any(static rule =>
                    string.IsNullOrWhiteSpace(rule.Phrase)
                    || rule.Replacement is null
                    || !Enum.IsDefined(rule.Category)))
            {
                return null;
            }

            return ruleSet with { Language = normalizedLanguage };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
