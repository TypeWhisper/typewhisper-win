using TypeWhisper.Presentation;
using Xunit;

public sealed class ScriptSyntaxTests
{
    [Fact]
    public void StringsAndCommentsDoNotLeakTheirContentsIntoVariableTokens()
    {
        const string code = "$text = 'Öl $notVariable'; # $alsoNotVariable\n[Console]::Write($text)";
        var tokens = ScriptSyntax.Tokenize(code, "powershell");
        Assert.Equal(2, tokens.Count(t => t.Kind == ScriptTokenKind.Variable));
        Assert.Contains(tokens, t => t.Kind == ScriptTokenKind.String && code.Substring(t.Start, t.Length) == "'Öl $notVariable'");
        Assert.Contains(tokens, t => t.Kind == ScriptTokenKind.Comment && code.Substring(t.Start, t.Length) == "# $alsoNotVariable");
        for (var i = 1; i < tokens.Count; i++) Assert.True(tokens[i - 1].Start + tokens[i - 1].Length <= tokens[i].Start);
    }

    [Theory]
    [InlineData("powershell", "$ä = \"line`\"quoted\"; if ($ä) { Write-Output $ä }")]
    [InlineData("pwsh", "<# multiline\ncomment #>\n$😀 = 'Öl'")]
    [InlineData("cmd", "@echo %TYPEWHISPER_LANGUAGE%\r\nrem test")]
    [InlineData("json", "{\"text\":\"Äpfel\\nÖl\",\"enabled\":true}")]
    [InlineData("markdown", "- [ ] Äpfel\n- [x] Öl\n> Zitat")]
    public void SpansStayWithinOriginalUtf16Text(string language, string code)
    {
        var tokens = ScriptSyntax.Tokenize(code, language);
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => { Assert.InRange(t.Start, 0, code.Length - 1); Assert.InRange(t.Length, 1, code.Length - t.Start); });
    }

    [Fact]
    public void UnknownOrOversizedTextRemainsPlain()
    {
        Assert.Empty(ScriptSyntax.Tokenize("$code", "unknown"));
        Assert.Empty(ScriptSyntax.Tokenize(new string('x', 32769), "powershell"));
    }

    [Fact]
    public void UndoIgnoresColorChangesPreservesWhitespaceAndBranchesAfterNewEdits()
    {
        var history = new ScriptEditHistory("Äpfel\n\n");
        history.Record("Äpfel\n\n"); Assert.False(history.CanUndo);
        history.Record("Öl\n"); history.Record("Öl\n");
        Assert.Equal("Äpfel\n\n", history.Undo()); Assert.False(history.CanUndo);
        Assert.Equal("Öl\n", history.Redo());
        history.Undo(); history.Record("Brot"); Assert.False(history.CanRedo);
        Assert.Equal("Äpfel\n\n", history.Undo());
    }

    [Fact]
    public void FormatterKeepsQuotedSemicolonsForHeadersCommentsAndEscapes()
    {
        const string code = "$text = 'one;two'; for ($i=0; $i -lt 2; $i++) { Write-Output $text; Write-Output a`;b }; # keep; comment";
        const string expected = "$text = 'one;two';\nfor ($i=0; $i -lt 2; $i++) { Write-Output $text;\n    Write-Output a`;b };\n# keep; comment";
        Assert.Equal(expected, ScriptSyntax.FormatPowerShell(code));
        Assert.Equal(expected, ScriptSyntax.FormatPowerShell(expected));
    }

    [Fact]
    public void UndoMemoryIsBounded()
    {
        var history = new ScriptEditHistory("");
        for (var i = 0; i < 200; i++) history.Record(i.ToString());
        var count = 0; while (history.CanUndo) { history.Undo(); count++; }
        Assert.Equal(99, count);
    }
}
