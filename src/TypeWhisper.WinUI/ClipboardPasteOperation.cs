namespace TypeWhisper.WinUI;

internal interface IClipboardPastePlatform
{
    bool CanPaste { get; }
    Task<bool> VerifyFieldAsync() => Task.FromResult(true);
    Task<IDisposable> BeginAsync(string text);
    bool ClipboardIsOwned { get; }
    uint SendPaste();
    Task WaitForPasteAsync();
    /// <summary>Records the focused field before Ctrl+V, so the paste can be confirmed before Enter. Never throws: a field
    /// that cannot be recorded leaves Enter to the later checks.</summary>
    Task PrepareSubmitAsync();
    /// <summary>Waits until the same field shows the pasted text; false when it changed or the paste never appeared.</summary>
    Task<bool> WaitForSubmitAsync();
    /// <summary>Releases a field recorded for a paste that was not sent.</summary>
    void DiscardSubmit();
    /// <summary>Sends Enter down and up, releasing it after a partial send; returns the number of events sent.</summary>
    uint SendSubmit();
    Task RestoreAsync(IDisposable lease);
}

/// <summary>Result of a paste; <paramref name="Restored"/> completes once the previous clipboard was restored.
/// <paramref name="Submitted"/> reports a requested Enter that followed the paste.</summary>
internal readonly record struct ClipboardPasteResult(bool Inserted, Task Restored, bool Submitted = false);

internal static class ClipboardPasteOperation
{
    /// <summary>
    /// Returns as soon as Ctrl+V was sent, or with <paramref name="submit"/> once Enter followed it. Only a complete
    /// paste defers the restore to <see cref="ClipboardPasteResult.Restored"/>; rejected, partial and failed pastes
    /// restore before returning.
    /// </summary>
    internal static async Task<ClipboardPasteResult> RunAsync(IClipboardPastePlatform platform, string text, bool submit = false)
    {
        if (!await platform.VerifyFieldAsync() || !platform.CanPaste)
            return new(false, Task.CompletedTask);
        var lease = await platform.BeginAsync(text);
        uint sent = 0;
        try
        {
            if (submit) await platform.PrepareSubmitAsync();
            if (await platform.VerifyFieldAsync() && platform.CanPaste && platform.ClipboardIsOwned)
                sent = platform.SendPaste();
        }
        catch
        {
            if (submit) platform.DiscardSubmit();
            await RestoreAsync(platform, lease, sent, null);
            throw;
        }
        if (sent != 4)
        {
            if (submit) platform.DiscardSubmit();
            await RestoreAsync(platform, lease, sent, null);
            return new(false, Task.CompletedTask);
        }
        var submitted = submit ? SubmitAsync(platform) : null;
        // The clipboard keeps the text until Enter was decided, so a slow target still pastes the dictation.
        var restored = RestoreAsync(platform, lease, sent, submitted);
        return new(true, restored, submitted is not null && await submitted);
    }

    // Enter follows only once the paste reached the same field, while the clipboard still holds the text. A failed
    // check leaves the pasted text unsent rather than turning the paste into a failure. A sent key-down may already
    // have submitted, so it counts as sent.
    private static async Task<bool> SubmitAsync(IClipboardPastePlatform platform)
    {
        try
        {
            return await platform.WaitForSubmitAsync() && await platform.VerifyFieldAsync() && platform.CanPaste
                && platform.SendSubmit() > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static async Task RestoreAsync(IClipboardPastePlatform platform, IDisposable lease, uint sent, Task<bool>? submitted)
    {
        using (lease)
        {
            // Do not restore before the target has had a chance to consume Ctrl+V.
            try
            {
                if (sent > 0) await platform.WaitForPasteAsync();
                if (submitted is not null) await submitted;
            }
            finally { await platform.RestoreAsync(lease); }
        }
    }
}
