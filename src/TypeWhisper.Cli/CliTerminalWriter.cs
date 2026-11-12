using System.Text;

namespace TypeWhisper.Cli;

/// <summary>
/// Writes to an interactive console without control characters. History text, model names and server messages
/// are printed as they arrive, and escape sequences in them would otherwise change the terminal title, rewrite the
/// screen or set the clipboard. Tab, line feed and carriage return pass; redirected output stays byte for byte.
/// </summary>
public sealed class CliTerminalWriter(TextWriter inner) : TextWriter
{
    /// <inheritdoc/>
    public override Encoding Encoding => inner.Encoding;

    /// <inheritdoc/>
    public override IFormatProvider FormatProvider => inner.FormatProvider;

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string NewLine { get => inner.NewLine; set => inner.NewLine = value; }

    /// <summary>Replaces characters a terminal would interpret as commands.</summary>
    public static string Clean(string value)
    {
        var index = 0;
        while (index < value.Length && !IsControl(value[index])) index++;
        if (index == value.Length) return value;
        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, index);
        for (; index < value.Length; index++) builder.Append(IsControl(value[index]) ? Replacement : value[index]);
        return builder.ToString();
    }

    // U+FFFD, the Unicode replacement character.
    private const char Replacement = (char)0xFFFD;

    private static bool IsControl(char value) =>
        value is < ' ' and not ('\t' or '\n' or '\r') or '\u007F' or >= '\u0080' and <= '\u009F';

    /// <inheritdoc/>
    public override void Write(char value) => inner.Write(IsControl(value) ? Replacement : value);

    /// <inheritdoc/>
    public override void Write(string? value)
    {
        if (value is not null) inner.Write(Clean(value));
    }

    /// <inheritdoc/>
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    /// <inheritdoc/>
    public override void WriteLine(string? value)
    {
        Write(value);
        inner.WriteLine();
    }

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Flush();
        base.Dispose(disposing);
    }
}
