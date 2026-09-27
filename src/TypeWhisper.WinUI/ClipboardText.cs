using global::Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

internal static class ClipboardText
{
    // Another process can hold the clipboard; callers must only report a copy that succeeded.
    internal static bool TrySet(string text)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
}
