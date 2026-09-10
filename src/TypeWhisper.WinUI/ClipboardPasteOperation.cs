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

internal static class ClipboardPasteOperation
{
    internal static async Task<bool> RunAsync(IClipboardPastePlatform platform, string text)
    {
        if (!platform.CanPaste) return false;
        using var lease = await platform.BeginAsync(text);
        uint sent = 0;
        try
        {
            if (!platform.CanPaste || !platform.ClipboardIsOwned) return false;
            sent = platform.SendPaste();
            return sent == 4;
        }
        finally
        {
            // Do not restore before the target has had a chance to consume Ctrl+V.
            try { if (sent > 0) await platform.WaitForPasteAsync(); }
            finally { await platform.RestoreAsync(lease); }
        }
    }
}
