using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TypeWhisper.Presentation;

/// <summary>Text around the selection of the field that receives a dictation.</summary>
/// <param name="Before">Text before the selection start, at most <see cref="DictationInsertionText.ContextLength"/> characters.</param>
/// <param name="After">Text after the selection end, at most <see cref="DictationInsertionText.ContextLength"/> characters.</param>
public sealed record DictationInsertionContext(string Before, string After);

/// <summary>Fits dictated text to the cursor position, as the macOS app does.</summary>
/// <remarks>Only the pasted text changes. History, API results, readback and action plugins keep the final text.</remarks>
public static class DictationInsertionText
{
    /// <summary>How much text on each side of the selection the formatter looks at.</summary>
    public const int ContextLength = 256;

    /// <summary>Returns the text to paste for the given field context.</summary>
    /// <remarks>Without context the text stays unchanged. With context, spaces at the boundaries are trimmed (line
    /// breaks and tabs from spoken commands stay), a missing space between words is added, and a dictation
    /// continuing a sentence starts lowercase and loses a final period when more text follows. A value such as an email address inserted into an empty field loses a
    /// model-added final period when <paramref name="standaloneValueFinalPeriodCleanup"/> is set.</remarks>
    public static string ForPaste(string text, DictationInsertionContext? context, bool standaloneValueFinalPeriodCleanup = true)
    {
        if (context is null) return text;
        var before = Elements(context.Before);
        var after = Elements(context.After);
        var previous = before.Count > 0 ? before[^1] : null;
        var next = after.Count > 0 ? after[0] : null;
        var previousNonWhitespace = before.LastOrDefault(element => !IsWhitespace(element));
        var nextNonWhitespace = after.FirstOrDefault(element => !IsWhitespace(element));
        var midSentence = previousNonWhitespace is not null && IsWordLike(previousNonWhitespace);

        var result = Elements(text);
        // Spoken "new line" or "tab" commands at either end are kept; plain spaces there are not. Unlike macOS,
        // which trims both.
        var leading = BoundaryBreaks(result, fromStart: true);
        var trailing = BoundaryBreaks(result, fromStart: false);
        TrimWhitespace(result);
        // A kept break starts a new line before the dictation or ends its sentence before the following text.
        if (midSentence && leading.Count == 0) LowercaseFirstWordIfSafe(result);
        if (midSentence && trailing.Count == 0 && nextNonWhitespace is not null && (IsWordLike(nextNonWhitespace) || ClosingPunctuation.Contains(nextNonWhitespace)))
            StripSingleFinalPeriod(result);
        // Mutually exclusive with the mid-sentence rule above: that one needs surrounding text, this one needs none.
        if (standaloneValueFinalPeriodCleanup && previousNonWhitespace is null && nextNonWhitespace is null &&
            StandaloneValue.ShouldStripFinalPeriod(string.Concat(result)))
            StripSingleFinalPeriod(result);

        result.InsertRange(0, leading);
        result.AddRange(trailing);
        if (result.Count == 0) return "";
        var formatted = string.Concat(result);
        if (previous is not null && ShouldInsertSpace(before.Count > 1 ? before[^2] : null, previous, result[0]))
            formatted = " " + formatted;
        if (next is not null && ShouldInsertSpace(result.Count > 1 ? result[^2] : previous, result[^1], next)) formatted += " ";
        return formatted;
    }

    private static readonly HashSet<string> OpeningPunctuation = ["(", "[", "{", "\"", "'", "“", "‘"];
    private static readonly HashSet<string> ClosingPunctuation = [".", ",", "!", "?", ";", ":", ")", "]", "}", "\"", "'", "”", "’"];
    private static readonly HashSet<string> PunctuationThatTakesFollowingSpace = [".", ",", "!", "?", ";", ":", ")", "]", "}", "\"", "'", "”", "’"];

    private static bool ShouldInsertSpace(string? beforeLeft, string left, string right)
    {
        if (IsWhitespace(left) || IsWhitespace(right)) return false;
        if (ClosingPunctuation.Contains(right) || (OpeningPunctuation.Contains(left) && !ClosesQuote(beforeLeft, left))) return false;
        if (IsCjk(left) && IsCjk(right)) return false;
        if (IsWordLike(left) && IsWordLike(right)) return true;
        return IsWordLike(right) && PunctuationThatTakesFollowingSpace.Contains(left);
    }

    // A straight double quote right after a word or the end of a phrase closes a quotation, as in `He said "hello."`,
    // and takes a space before the next word. After a delimiter such as `:` or `=` it opens one, as in `{"key":"`.
    // macOS treats it as opening everywhere. Apostrophes stay ambiguous because of elisions such as "l'".
    private static bool ClosesQuote(string? beforeQuote, string quote) =>
        quote == "\"" && beforeQuote is not null && (IsWordLike(beforeQuote) || EndsQuotedPhrase.Contains(beforeQuote));
    private static readonly HashSet<string> EndsQuotedPhrase = [".", ",", "!", "?", ")", "]", "}", "”", "’"];

    // Lowercases "Presented" but keeps "NASA", "TypeWhisper" and single letters such as "I".
    private static void LowercaseFirstWordIfSafe(List<string> text)
    {
        var start = text.FindIndex(element => !IsWhitespace(element));
        if (start < 0 || !IsWordLike(text[start])) return;
        var end = start + 1;
        while (end < text.Count && IsWordLike(text[end])) end++;
        if (end - start < 2 || !IsUppercase(text[start])) return;
        var remainder = text.GetRange(start + 1, end - start - 1);
        if (!remainder.Any(IsLowercase) || remainder.Any(IsUppercase)) return;
        // Full case mapping as in Swift: U+0130 is the one letter whose lowercase form is two characters.
        text[start] = text[start].ToLowerInvariant().Replace("\u0130", "i\u0307", StringComparison.Ordinal);
    }

    // Removes one final period of trimmed text, but never part of an ellipsis.
    private static void StripSingleFinalPeriod(List<string> text)
    {
        if (text.Count == 0 || text[^1] != "." || (text.Count > 1 && text[^2] == ".")) return;
        text.RemoveAt(text.Count - 1);
    }

    // Line breaks and tabs in the whitespace run at one end of the text, without the spaces around them.
    private static List<string> BoundaryBreaks(List<string> text, bool fromStart)
    {
        var run = fromStart ? text.TakeWhile(IsWhitespace) : text.AsEnumerable().Reverse().TakeWhile(IsWhitespace).Reverse();
        // A text of only whitespace keeps its breaks once, at the start.
        if (!fromStart && text.All(IsWhitespace)) return [];
        return run.Where(element => element.Any(c => c is '\n' or '\r' or '\t' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029')).ToList();
    }

    private static void TrimWhitespace(List<string> text)
    {
        while (text.Count > 0 && IsWhitespace(text[^1])) text.RemoveAt(text.Count - 1);
        while (text.Count > 0 && IsWhitespace(text[0])) text.RemoveAt(0);
    }

    // User-perceived characters, so a letter with a combining mark or an emoji sequence counts once.
    private static List<string> Elements(string text)
    {
        var elements = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        return elements;
    }

    private static bool IsWhitespace(string element) => element.EnumerateRunes().All(Rune.IsWhiteSpace);
    private static bool IsWordLike(string element) => element.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or
        UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber or
        UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber);
    private static bool IsUppercase(string element) => element.EnumerateRunes().Any(rune =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter);
    private static bool IsLowercase(string element) => element.EnumerateRunes().Any(rune =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.LowercaseLetter);

    // Han, Hiragana, Katakana and Hangul words are not separated by spaces. Like macOS, this uses the base character's
    // Script_Extensions, so shared marks such as the prolonged sound mark ー count, while a combining mark after a
    // Latin letter does not.
    private static bool IsCjk(string element)
    {
        var enumerator = element.EnumerateRunes();
        if (!enumerator.MoveNext()) return false;
        var value = enumerator.Current.Value;
        int low = 0, high = CjkRanges.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (value < CjkRanges[middle].First) high = middle - 1;
            else if (value > CjkRanges[middle].Last) low = middle + 1;
            else return true;
        }
        return false;
    }

    // Code points whose Script_Extensions include Han, Hiragana, Katakana or Hangul (Unicode 18.0 Scripts.txt and
    // ScriptExtensions.txt).
    private static readonly (int First, int Last)[] CjkRanges =
    [
        (0xB7, 0xB7), (0x305, 0x305), (0x323, 0x323), (0x1100, 0x11FF), (0x2E80, 0x2E99), (0x2E9B, 0x2EF3),

        (0x2F00, 0x2FD5), (0x2FF0, 0x2FFF), (0x3001, 0x3003), (0x3005, 0x3011), (0x3013, 0x301F), (0x3021, 0x3035),

        (0x3037, 0x303F), (0x3041, 0x3096), (0x3099, 0x30FF), (0x3131, 0x318E), (0x3190, 0x319F), (0x31C0, 0x31E5),

        (0x31EF, 0x321E), (0x3220, 0x3247), (0x3260, 0x327E), (0x3280, 0x32B0), (0x32C0, 0x32CB), (0x32D0, 0x3370),

        (0x337B, 0x337F), (0x33E0, 0x33FE), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA700, 0xA707), (0xA960, 0xA97C),

        (0xAC00, 0xD7A3), (0xD7B0, 0xD7C6), (0xD7CB, 0xD7FB), (0xF900, 0xFA6D), (0xFA70, 0xFAD9), (0xFE45, 0xFE46),

        (0xFF61, 0xFFBE), (0xFFC2, 0xFFC7), (0xFFCA, 0xFFCF), (0xFFD2, 0xFFD7), (0xFFDA, 0xFFDC), (0x16FE2, 0x16FE3),

        (0x16FF0, 0x16FF6), (0x1AFF0, 0x1AFF3), (0x1AFF5, 0x1AFFB), (0x1AFFD, 0x1AFFE), (0x1B000, 0x1B128),

        (0x1B132, 0x1B132), (0x1B150, 0x1B152), (0x1B155, 0x1B155), (0x1B164, 0x1B168), (0x1D360, 0x1D371),

        (0x1F200, 0x1F200), (0x1F250, 0x1F251), (0x20000, 0x2A6DF), (0x2A700, 0x2B81E), (0x2B820, 0x2CEAD),

        (0x2CEB0, 0x2EBE0), (0x2EBF0, 0x2EE5D), (0x2F800, 0x2FA1D), (0x30000, 0x3134A), (0x31350, 0x33479)
    ];

    /// <summary>Decides whether a transcript standing on its own is a value whose final period the model added,
    /// as in <c>name@example.com.</c> dictated into an empty field.</summary>
    /// <remarks>Conservative on purpose: only whole-text email addresses, bare-domain URLs, decimal numbers, phone
    /// numbers and version strings qualify. Abbreviations, prose, dates and URLs with a path, query or fragment keep
    /// their period, because a dot is a legal part of those (RFC 3986 section 2.3).</remarks>
    private static class StandaloneValue
    {
        private const RegexOptions Options = RegexOptions.CultureInvariant;
        // Each dot-separated group is a single letter or a known abbreviation, so "file.txt." still counts as a value.
        private static readonly Regex Abbreviation = new(
            @"^(?:(?:[A-Za-z]|Dr|Mr|Mrs|Ms|No|St|Jr|Sr|Prof|Inc|Ltd|Co|etc|vs|bzw|ca|ggf|evtl|Nr|Tel|med|rer|nat|ing|dipl|phil|Ph)\.)+$",
            Options | RegexOptions.IgnoreCase);
        private static readonly Regex Email = new(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", Options);
        private static readonly Regex WebAddress = new(
            @"^(?:https?://|ftp://|www\.)?(?:[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,}(?::\d+)?$",
            Options | RegexOptions.IgnoreCase);
        // 3.14, 1,5, 1,000.50 and 1.000,50: English and German forms.
        private static readonly Regex Decimal = new(@"^\d{1,3}(?:[.,]\d{3})+(?:[.,]\d+)?$|^\d+[.,]\d+$", Options);
        // Dictation often spaces the separators ("27 / 09 / 2026"); such dates must not pass as phone numbers.
        private static readonly Regex Date = new(
            @"^\d{1,2}\s*[./\-]\s*\d{1,2}\s*[./\-]\s*\d{2,4}$|^\d{4}\s*[./\-]\s*\d{1,2}\s*[./\-]\s*\d{1,2}$", Options);
        private static readonly Regex Version = new(@"^v?\d+(?:\.\d+){2,}$", Options | RegexOptions.IgnoreCase);
        private static readonly Regex Phone = new(@"^[+\d(][\d\s\-/.()]*$", Options);

        internal static bool ShouldStripFinalPeriod(string text)
        {
            if (!text.EndsWith('.') || text.EndsWith("..", StringComparison.Ordinal)) return false;
            var candidate = text[..^1];
            if (candidate.Length == 0 || Abbreviation.IsMatch(text) || Date.IsMatch(candidate)) return false;
            return Email.IsMatch(candidate) || WebAddress.IsMatch(candidate) || Decimal.IsMatch(candidate) ||
                Version.IsMatch(candidate) || (Phone.IsMatch(candidate) && candidate.Count(char.IsDigit) >= 6);
        }
    }
}
