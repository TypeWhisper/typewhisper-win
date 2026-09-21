namespace TypeWhisper.Plugin.Script;

internal sealed record ScriptTemplate(string Id, string EnglishName, string GermanName, string EnglishDescription, string GermanDescription, string Command)
{
    internal ScriptEntry Create(bool german) => new()
    {
        Name = german ? GermanName : EnglishName,
        Shell = ScriptShells.WindowsPowerShell,
        Command = Command,
        IsEnabled = false,
        TimeoutSeconds = 10
    };
}

internal static class ScriptTemplates
{
    internal static readonly IReadOnlyList<ScriptTemplate> All =
    [
        new("uppercase", "UPPERCASE", "GROSSBUCHSTABEN", "Convert the entire text to uppercase.", "Den gesamten Text in Großbuchstaben umwandeln.",
            "[Console]::Out.Write([Console]::In.ReadToEnd().ToUpperInvariant())"),
        new("lowercase", "lowercase", "kleinbuchstaben", "Convert the entire text to lowercase.", "Den gesamten Text in Kleinbuchstaben umwandeln.",
            "[Console]::Out.Write([Console]::In.ReadToEnd().ToLowerInvariant())"),
        new("trim", "Trim whitespace", "Leerraum am Rand entfernen", "Remove whitespace before and after the text.", "Leerraum vor und nach dem Text entfernen.",
            "[Console]::Out.Write([Console]::In.ReadToEnd().Trim())"),
        new("clean-spacing", "Clean up spacing", "Leerzeichen bereinigen", "Collapse repeated spaces and tabs; preserve paragraphs.", "Mehrfache Leerzeichen und Tabs bereinigen; Absätze erhalten.",
            """$text = [Console]::In.ReadToEnd(); $lines = $text -split '\r?\n' | ForEach-Object { ($_ -replace '[\t ]+', ' ').Trim() }; [Console]::Out.Write(($lines -join [Environment]::NewLine).Trim())"""),
        new("bullets", "Markdown bullet list", "Markdown-Aufzählung", "Turn each non-empty line into a bullet. Existing bullets are preserved.", "Jede nicht leere Zeile als Listenpunkt formatieren. Vorhandene Listenpunkte bleiben erhalten.",
            """$lines = [Console]::In.ReadToEnd() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $line = $_.Trim(); if ($line -match '^[-*+]\s+') { $line } else { '- ' + $line } }; [Console]::Out.Write($lines -join [Environment]::NewLine)"""),
        new("checklist", "Markdown checklist", "Markdown-Checkliste", "Turn each non-empty line into a task; preserve existing checkboxes.", "Jede nicht leere Zeile als Aufgabe formatieren; vorhandene Kontrollkästchen erhalten.",
            """$lines = [Console]::In.ReadToEnd() -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $line = $_.Trim(); if ($line -match '^[-*+]\s+\[[ xX]\]\s*') { $line } else { '- [ ] ' + ($line -replace '^[-*+]\s+', '') } }; [Console]::Out.Write($lines -join [Environment]::NewLine)"""),
        new("quote", "Markdown quotation", "Markdown-Zitat", "Quote every line, including paragraph breaks.", "Alle Zeilen einschließlich Absatzumbrüchen als Zitat formatieren.",
            """$lines = [Console]::In.ReadToEnd() -split '\r?\n' | ForEach-Object { if ($_ -match '^>\s?') { $_ } else { '> ' + $_ } }; [Console]::Out.Write($lines -join [Environment]::NewLine)"""),
        new("json", "JSON string", "JSON-Zeichenfolge", "Escape text as a JSON string, including quotes and line breaks.", "Text als JSON-Zeichenfolge mit korrekt maskierten Anführungszeichen und Zeilenumbrüchen ausgeben.",
            "[Console]::Out.Write((ConvertTo-Json -InputObject ([Console]::In.ReadToEnd()) -Compress))"),
        new("url", "URL-encode text", "Text für URLs kodieren", "Encode a query parameter value, not a complete URL.", "Einen Parameterwert für eine URL kodieren, keine vollständige URL.",
            "[Console]::Out.Write([Uri]::EscapeDataString([Console]::In.ReadToEnd()))")
    ];
}
