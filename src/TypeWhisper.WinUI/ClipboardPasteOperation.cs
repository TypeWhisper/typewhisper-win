namespace TypeWhisper.WinUI;

internal interface IClipboardPastePlatform
{
    bool CanPaste { get; }
    Task<bool> VerifyFieldAsync() => Task.FromResult(true);
    Task<IDisposable> BeginAsync(string text);
    bool ClipboardIsOwned { get; }
    uint SendPaste();
    Task WaitForPasteAsync();
    /// <summary>Waits until the target has inserted the pasted text, before Enter submits it.</summary>
    Task WaitForSubmitAsync();
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
            if (await platform.VerifyFieldAsync() && platform.CanPaste && platform.ClipboardIsOwned)
                sent = platform.SendPaste();
        }
        catch
        {
            await RestoreAsync(platform, lease, sent);
            throw;
        }
        var restored = RestoreAsync(platform, lease, sent);
        if (sent == 4) return new(true, restored, submit && await SubmitAsync(platform));
        await restored;
        return new(false, Task.CompletedTask);
    }

    // Enter follows within the restore delay, while the clipboard still holds the text, and only into the same
    // field. A failed check leaves the pasted text unsent rather than turning the paste into a failure. A sent
    // key-down may already have submitted, so it counts as sent.
    private static async Task<bool> SubmitAsync(IClipboardPastePlatform platform)
    {
        try
        {
            await platform.WaitForSubmitAsync();
            return await platform.VerifyFieldAsync() && platform.CanPaste && platform.SendSubmit() > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static async Task RestoreAsync(IClipboardPastePlatform platform, IDisposable lease, uint sent)
    {
        using (lease)
        {
            // Do not restore before the target has had a chance to consume Ctrl+V.
            try { if (sent > 0) await platform.WaitForPasteAsync(); }
            finally { await platform.RestoreAsync(lease); }
        }
    }
}
