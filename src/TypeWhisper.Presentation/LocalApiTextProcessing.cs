namespace TypeWhisper.Presentation;

/// <summary>Applies API-request translation and corrections without running app workflows or snippets.</summary>
public static class LocalApiTextProcessing
{
    /// <summary>Translates before dictionary correction and rejects canceled or empty output.</summary>
    public static async Task<string> ProcessAsync(string text, bool applyCorrections,
        Func<string, CancellationToken, Task<string>>? translate, Func<string, string>? correct,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (translate is not null) text = await translate(text, ct);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Translation returned no text.");
        if (applyCorrections && correct is not null) text = correct(text);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Corrections returned no text.");
        return text;
    }
}
