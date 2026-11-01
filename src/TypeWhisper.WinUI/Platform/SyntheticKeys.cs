namespace TypeWhisper.WinUI.Platform;

// Builds the keys TypeWhisper sends itself (paste, copy, Enter) the way a real keyboard reports them.
// Flutter reads the physical key from the scan code. Without one, Ctrl and V look like the same key,
// Flutter releases Ctrl before V, and the app receives a plain "v" instead of a paste.
internal readonly struct SyntheticKeys(uint targetThread)
{
    // The target's own layout, so the scan codes match what its keyboard would send.
    // Thread 0 (target gone) falls back to this thread's layout.
    private readonly IntPtr _layout = NativeMethods.GetKeyboardLayout(targetThread);

    internal NativeMethods.Input Down(ushort key) => Create(key, 0);
    internal NativeMethods.Input Up(ushort key) => Create(key, NativeMethods.KEYEVENTF_KEYUP);

    private NativeMethods.Input Create(ushort key, uint flags)
    {
        var scan = NativeMethods.MapVirtualKeyEx(key, NativeMethods.MAPVK_VK_TO_VSC_EX, _layout);
        // Extended keys come back with an 0xE0 prefix, which SendInput takes as a flag instead.
        if ((scan & 0xFF00) == 0xE000) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        return new()
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            Data = new() { Keyboard = new() { Key = key, Scan = (ushort)(scan & 0xFF), Flags = flags, Extra = OwnKeyboardInput.Marker } }
        };
    }
}
