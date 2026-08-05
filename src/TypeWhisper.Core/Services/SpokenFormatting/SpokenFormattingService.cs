using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>
/// Replaces visible spoken punctuation and structural commands.
/// </summary>
public sealed class SpokenFormattingService
{
    private static readonly Regex SpacesBeforeLineBreak = new(@"[ \f\v]+(?=\r\n|\r|\n)", RegexOptions.CultureInvariant);
    private static readonly Regex SpacesAfterLineBreak = new(@"(\r\n|\r|\n)[ \f\v]+", RegexOptions.CultureInvariant);
    private static readonly Regex SpacesAroundTab = new(@" *\t *", RegexOptions.CultureInvariant);
    private static readonly Regex SpacesBeforeClosingToken = new(@" +([,\.:;?!\)\]\}])", RegexOptions.CultureInvariant);
    private static readonly Regex SpacesAfterOpeningToken = new(@"([\(\[\{]) +", RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedNonPeriodPunctuation = new(@"([,:;?!])(?:\s*\1)+", RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedPeriodPair = new(@"(?<!\.)\.\.(?!\.)", RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedSpaces = new(@" {2,}", RegexOptions.CultureInvariant);
    private readonly SpokenFormattingRulesLoader _rulesLoader;
    private readonly ConcurrentDictionary<string, SpokenFormattingRule[]> _orderedRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    /// <summary>Initializes a new spoken formatting service.</summary>
    public SpokenFormattingService(SpokenFormattingRulesLoader rulesLoader)
    {
        _rulesLoader = rulesLoader;
    }

    /// <summary>Applies the configured formatting behavior for a supported language.</summary>
    public string Normalize(
        string text,
        string? language,
        SpokenFormattingApplicationMode mode = SpokenFormattingApplicationMode.FullFallback)
    {
        if (string.IsNullOrEmpty(text) || _rulesLoader.RuleSetFor(language) is not { } ruleSet)
            return text;

        var result = text;
        var replacementApplied = false;
        foreach (var rule in OrderedRules(ruleSet))
            result = ReplaceRule(result, rule, ref replacementApplied);

        if (mode == SpokenFormattingApplicationMode.SelectiveFallback && !replacementApplied)
            return text;

        return NormalizeSpacing(result);
    }

    private SpokenFormattingRule[] OrderedRules(SpokenFormattingRuleSet ruleSet) =>
        _orderedRules.GetOrAdd(ruleSet.Language, _ =>
        [
            .. ruleSet.Rules
                .Where(static rule => rule.Category == SpokenFormattingRuleCategory.Structural)
                .OrderByDescending(static rule => rule.Phrase.Length),
            .. ruleSet.Rules
                .Where(static rule => rule.Category != SpokenFormattingRuleCategory.Structural)
                .OrderByDescending(static rule => rule.Phrase.Length)
        ]);

    private string ReplaceRule(string text, SpokenFormattingRule rule, ref bool replacementApplied)
    {
        var regex = _patterns.GetOrAdd(
            $"{rule.Category}:{rule.Phrase}",
            _ => BuildPattern(rule));
        var changed = false;
        var result = regex.Replace(text, match =>
        {
            changed = true;
            if (rule.Category == SpokenFormattingRuleCategory.Punctuation
                && ShouldSuppressDuplicatePunctuation(text, match, rule.Replacement))
            {
                return "";
            }

            return rule.Replacement;
        });

        replacementApplied |= changed;
        return result;
    }

    private static Regex BuildPattern(SpokenFormattingRule rule)
    {
        var escapedWords = Regex.Split(rule.Phrase.Trim(), @"\s+")
            .Where(static word => word.Length > 0)
            .Select(Regex.Escape);
        var phrasePattern = string.Join(@"\s+", escapedWords);
        var attachedPunctuation = rule.Category == SpokenFormattingRuleCategory.Structural
            ? @"[\.,:;?!]?"
            : "";
        var pattern = $@"(?<![\p{{L}}\p{{M}}\p{{N}}]){phrasePattern}(?![\p{{L}}\p{{M}}\p{{N}}]){attachedPunctuation}";
        return new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }

    private static bool ShouldSuppressDuplicatePunctuation(string text, Match match, string replacement)
    {
        if (replacement.Length != 1)
            return false;

        var replacementCharacter = CanonicalPunctuation(replacement[0]);
        var previous = PreviousNonWhitespace(text, match.Index);
        var next = NextNonWhitespace(text, match.Index + match.Length);
        return previous is not null && CanonicalPunctuation(previous.Value) == replacementCharacter
            || next is not null && CanonicalPunctuation(next.Value) == replacementCharacter;
    }

    private static char? PreviousNonWhitespace(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return null;
    }

    private static char? NextNonWhitespace(string text, int index)
    {
        for (var i = index; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return null;
    }

    private static char CanonicalPunctuation(char character) => character switch
    {
        '。' => '.',
        '、' => ',',
        '？' => '?',
        '！' => '!',
        '：' => ':',
        '；' => ';',
        _ => character
    };

    private static string NormalizeSpacing(string text)
    {
        var result = SpacesBeforeLineBreak.Replace(text, "");
        result = SpacesAfterLineBreak.Replace(result, "$1");
        result = SpacesAroundTab.Replace(result, "\t");
        result = SpacesBeforeClosingToken.Replace(result, "$1");
        result = SpacesAfterOpeningToken.Replace(result, "$1");
        result = RepeatedNonPeriodPunctuation.Replace(result, "$1");
        result = RepeatedPeriodPair.Replace(result, ".");
        result = RepeatedSpaces.Replace(result, " ");
        return result.Trim(' ');
    }
}
