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
    /// <remarks>Without context the text stays unchanged. With context, boundary whitespace is trimmed, a missing
    /// space between words is added, and a dictation continuing a sentence starts lowercase and loses a final
    /// period when more text follows. A value such as an email address inserted into an empty field loses a
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
        TrimWhitespace(result);
        if (midSentence) LowercaseFirstWordIfSafe(result);
        if (midSentence && nextNonWhitespace is not null && (IsWordLike(nextNonWhitespace) || ClosingPunctuation.Contains(nextNonWhitespace)))
            StripSingleFinalPeriod(result);
        // Mutually exclusive with the mid-sentence rule above: that one needs surrounding text, this one needs none.
        if (standaloneValueFinalPeriodCleanup && previousNonWhitespace is null && nextNonWhitespace is null &&
            StandaloneValue.ShouldStripFinalPeriod(string.Concat(result)))
            StripSingleFinalPeriod(result);

        if (result.Count == 0) return "";
        var formatted = string.Concat(result);
        if (previous is not null && ShouldInsertSpace(previous, result[0])) formatted = " " + formatted;
        if (next is not null && ShouldInsertSpace(result[^1], next)) formatted += " ";
        return formatted;
    }

    private static readonly HashSet<string> OpeningPunctuation = ["(", "[", "{", "\"", "'", "“", "‘"];
    private static readonly HashSet<string> ClosingPunctuation = [".", ",", "!", "?", ";", ":", ")", "]", "}", "\"", "'", "”", "’"];
    private static readonly HashSet<string> PunctuationThatTakesFollowingSpace = [".", ",", "!", "?", ";", ":", ")", "]", "}", "\"", "'", "”", "’"];

    private static bool ShouldInsertSpace(string left, string right)
    {
        if (IsWhitespace(left) || IsWhitespace(right)) return false;
        if (ClosingPunctuation.Contains(right) || OpeningPunctuation.Contains(left)) return false;
        if (IsCjk(left) && IsCjk(right)) return false;
        if (IsWordLike(left) && IsWordLike(right)) return true;
        return IsWordLike(right) && PunctuationThatTakesFollowingSpace.Contains(left);
    }

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
        text[start] = text[start].ToLowerInvariant();
    }

    // Removes one final period, together with any whitespace after it, but never part of an ellipsis.
    private static void StripSingleFinalPeriod(List<string> text)
    {
        var last = text.FindLastIndex(element => !IsWhitespace(element));
        if (last < 0 || text[last] != "." || (last > 0 && text[last - 1] == ".")) return;
        text.RemoveRange(last, text.Count - last);
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

    // Han, Hiragana, Katakana and Hangul. Words in these scripts are not separated by spaces. Marks shared by
    // several scripts, such as the prolonged sound mark, belong to none of them.
    private static bool IsCjk(string element) => element.EnumerateRunes().Any(rune => rune.Value is not
        (0x309B or 0x309C or 0x30A0 or 0x30FB or 0x30FC or 0xFF70 or 0xFF9E or 0xFF9F) and
        ((>= 0x1100 and <= 0x11FF) or (>= 0x2E80 and <= 0x2FDF) or 0x3005 or 0x3007 or (>= 0x3021 and <= 0x3029) or
        (>= 0x3038 and <= 0x303B) or (>= 0x3041 and <= 0x30FF) or (>= 0x3130 and <= 0x318F) or (>= 0x31F0 and <= 0x31FF) or
        (>= 0x32D0 and <= 0x32FE) or (>= 0x3300 and <= 0x3357) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or
        (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF66 and <= 0xFFDC) or
        (>= 0x1AFF0 and <= 0x1B16F) or (>= 0x20000 and <= 0x3FFFF)));

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
