namespace TypeWhisper.WinUI;

internal interface IClipboardPastePlatform
{
    bool CanPaste { get; }
    Task<IDisposable> BeginAsync(string text);
    bool ClipboardIsOwned { get; }
    uint SendPaste();
    Task WaitForPasteAsync();
    Task RestoreAsync(IDisposable lease);
}

/// <summary>Result of a paste; <paramref name="Restored"/> completes once the previous clipboard was restored.</summary>
internal readonly record struct ClipboardPasteResult(bool Inserted, Task Restored);

internal static class ClipboardPasteOperation
{
    /// <summary>
    /// Returns as soon as Ctrl+V was sent. Only a complete paste defers the restore to
    /// <see cref="ClipboardPasteResult.Restored"/>; rejected, partial and failed pastes restore before returning.
    /// </summary>
    internal static async Task<ClipboardPasteResult> RunAsync(IClipboardPastePlatform platform, string text)
    {
        if (!platform.CanPaste) return new(false, Task.CompletedTask);
        var lease = await platform.BeginAsync(text);
        uint sent = 0;
        try
        {
            if (platform.CanPaste && platform.ClipboardIsOwned) sent = platform.SendPaste();
        }
        catch
        {
            await RestoreAsync(platform, lease, sent);
            throw;
        }
        var restored = RestoreAsync(platform, lease, sent);
        if (sent == 4) return new(true, restored);
        await restored;
        return new(false, Task.CompletedTask);
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
