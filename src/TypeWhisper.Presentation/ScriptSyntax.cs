using System.Text.RegularExpressions;

namespace TypeWhisper.Presentation;

/// <summary>Lexical colors for local script editing; highlighting never evaluates code.</summary>
public enum ScriptTokenKind
{
    /// <summary>Uncolored text.</summary>
    Plain,
    /// <summary>A comment.</summary>
    Comment,
    /// <summary>A quoted string.</summary>
    String,
    /// <summary>A variable reference.</summary>
    Variable,
    /// <summary>A language keyword or command.</summary>
    Keyword,
    /// <summary>A type literal.</summary>
    Type,
    /// <summary>A number.</summary>
    Number
}
/// <summary>A UTF-16 span in the original, unchanged editor text.</summary>
public readonly record struct ScriptToken(int Start, int Length, ScriptTokenKind Kind);

/// <summary>Lightweight, bounded lexical highlighting for PowerShell, cmd and result previews.</summary>
public static class ScriptSyntax
{
    private static readonly Regex PowerShell = new(
        """(?<Comment><#[\s\S]*?(?:#>|\z)|\#[^\r\n]*)|(?<String>--%(?:"[^"\r\n]*(?:"|(?=[\r\n]|\z))|[^"\r\n|])*|@'\r?\n[\s\S]*?(?:\r?\n'@|\z)|@"\r?\n[\s\S]*?(?:\r?\n"@|\z)|'(?:''|[^'])*(?:'|\z)|"(?:`[\s\S]|[^"`])*(?:"|\z))|(?<Variable>\$(?:\{[^}]*\}|[\w:?]+))|(?<Type>\[[\w.\[\],]+\])|(?<Keyword>\b(?:if|else|elseif|foreach|for|while|do|switch|return|function|param|try|catch|finally|throw|in|begin|process|end)\b|\b[A-Za-z]+-[A-Za-z]+\b|-(?:match|replace|split|join|notmatch|eq|ne|gt|lt|and|or|not)\b)|(?<Number>\b\d+(?:\.\d+)?\b)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Cmd = new(
        """(?<Comment>(?:^|[\r\n])\s*(?:rem\b|::)[^\r\n]*)|(?<String>"[^"\r\n]*(?:"|$))|(?<Variable>%[^%\r\n]+%|![^!\r\n]+!)|(?<Keyword>\b(?:echo|set|if|else|for|in|do|call|exit|goto|type|findstr|sort|endlocal|setlocal)\b)|(?<Number>\b\d+\b)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Json = new(
        """(?<String>"(?:\\.|[^"\\])*(?:"|\z))|(?<Keyword>\b(?:true|false|null)\b)|(?<Number>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Markdown = new(
        """(?<Keyword>^\s*(?:\#{1,6}\s|>\s?|[-*+]\s+(?:\[[ xX]\]\s*)?|\d+\.\s+))|(?<String>`[^`\r\n]+`)""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Adds line breaks at top-level statement separators, preserving strings, comments and for headers.</summary>
    public static string FormatPowerShell(string text)
    {
        if (text.Length > 32768) return text;
        ScriptToken[] protectedSpans;
        try
        {
            protectedSpans = PowerShell.Matches(text)
                .Where(match => match.Groups["String"].Success || match.Groups["Comment"].Success || match.Groups["Variable"].Success)
                .Select(match => new ScriptToken(match.Index, match.Length, ScriptTokenKind.Plain)).ToArray();
        }
        catch (RegexMatchTimeoutException) { return text; }
        var output = new System.Text.StringBuilder();
        var parentheses = 0;
        var braces = 0;
        var spanIndex = 0;
        for (var i = 0; i < text.Length; i++)
        {
            while (spanIndex < protectedSpans.Length && protectedSpans[spanIndex].Start + protectedSpans[spanIndex].Length <= i) spanIndex++;
            if (spanIndex < protectedSpans.Length && protectedSpans[spanIndex].Start == i)
            {
                var span = protectedSpans[spanIndex]; output.Append(text, i, span.Length); i += span.Length - 1; continue;
            }
            var c = text[i]; output.Append(c);
            if (c == '`' && i + 1 < text.Length) { output.Append(text[++i]); continue; }
            if (c == '(') parentheses++;
            if (c == ')') parentheses = Math.Max(0, parentheses - 1);
            if (c == '{') braces++;
            if (c == '}') braces = Math.Max(0, braces - 1);
            if (c != ';' || parentheses != 0) continue;
            var next = i + 1;
            while (next < text.Length && text[next] is ' ' or '\t') next++;
            if (next >= text.Length || text[next] is '\r' or '\n') continue;
            output.Append('\n').Append(' ', Math.Min(braces, 8) * 4);
            i = next - 1;
        }
        return output.ToString();
    }

    /// <summary>Returns non-overlapping colored spans, or plain text on a tokenization timeout.</summary>
    public static IReadOnlyList<ScriptToken> Tokenize(string text, string language)
    {
        if (text.Length > 32768) return [];
        var lexer = language switch { "powershell" or "pwsh" => PowerShell, "cmd" => Cmd, "json" => Json, "markdown" => Markdown, _ => null };
        if (lexer is null) return [];
        try
        {
            return lexer.Matches(text).Select(match => new ScriptToken(match.Index, match.Length,
                Enum.GetValues<ScriptTokenKind>().First(kind => kind != ScriptTokenKind.Plain && match.Groups[kind.ToString()].Success))).ToArray();
        }
        catch (RegexMatchTimeoutException) { return []; }
    }
}

/// <summary>Text-only undo history so syntax color updates cannot consume undo steps.</summary>
public sealed class ScriptEditHistory(string initial)
{
    private readonly List<string> _states = [initial];
    private int _index;
    /// <summary>Current plain text.</summary>
    public string Current => _states[_index];
    /// <summary>Whether a preceding text edit exists.</summary>
    public bool CanUndo => _index > 0;
    /// <summary>Whether an undone text edit exists.</summary>
    public bool CanRedo => _index + 1 < _states.Count;
    /// <summary>Records a changed text value, dropping redo states and bounding retained history.</summary>
    public void Record(string text)
    {
        if (text == Current) return;
        _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        _states.Add(text);
        if (_states.Count > 100) _states.RemoveAt(0);
        _index = _states.Count - 1;
    }
    /// <summary>Moves to the preceding text edit.</summary>
    public string Undo() { if (CanUndo) _index--; return Current; }
    /// <summary>Moves to the next text edit.</summary>
    public string Redo() { if (CanRedo) _index++; return Current; }
}
