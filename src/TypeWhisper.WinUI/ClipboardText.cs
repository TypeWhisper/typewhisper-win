using global::Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

internal static class ClipboardText
{
    // Another process can hold the clipboard; callers must only report a copy that succeeded. A copy the user did not
    // ask for, such as the text of a paste that failed, can stay out of clipboard history and the cloud clipboard; it
    // still pastes with Ctrl+V.
    internal static bool TrySet(string text, bool excludeFromHistory = false)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(text);
            // SetContentWithOptions reports a clipboard held by another process by returning false, not by throwing.
            if (excludeFromHistory)
                return Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
            Clipboard.SetContent(data);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
}
