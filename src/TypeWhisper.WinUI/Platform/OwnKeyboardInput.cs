namespace TypeWhisper.WinUI.Platform;

// Tags the keys TypeWhisper sends itself (paste, copy, media pause), so the dictation hook can
// skip those without also dropping shortcuts sent by gesture, macro or accessibility tools.
internal static class OwnKeyboardInput
{
    internal const uint Marker = 0x54575349;
    internal static bool Sent(uint flags, UIntPtr extra) => (flags & 0x10) != 0 && extra == Marker;
}
