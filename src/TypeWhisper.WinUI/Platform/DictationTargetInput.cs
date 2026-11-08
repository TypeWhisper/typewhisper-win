using System.Runtime.InteropServices;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Input into one dictation's target window for a plugin that delivers the dictation itself. Nothing is sent while
/// another window is in front. Pasting is delegated, so the dictation's paste and clipboard settings apply.
/// </summary>
internal sealed class DictationTargetInput(IntPtr target, Func<string, CancellationToken, Task<bool>> paste) : IDictationTargetInput
{
    private const uint WM_NULL = 0x0000, SMTO_ABORTIFHUNG = 0x0002;
    private static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    // Alt and Windows turn pressed keys into other shortcuts.
    private static readonly int[] ChangingModifiers = [0x12, 0x5B, 0x5C];

    private bool InFront => target != IntPtr.Zero && NativeMethods.GetForegroundWindow() == target;

    public bool IsReady => InFront && !Modifiers.Any(Held);

    public bool PressKeys(IReadOnlyList<ushort> virtualKeys) => IsReady && SendChord(virtualKeys);

    // A Ctrl or Shift the user holds is left alone: releasing it would end a push-to-talk shortcut.
    public bool SignalKeys(IReadOnlyList<ushort> virtualKeys)
    {
        if (!InFront || ChangingModifiers.Any(Held)) return false;
        var missing = virtualKeys.Where(key => !Held(key)).ToArray();
        return missing.Length > 0 && SendChord(missing);
    }

    public async Task<bool> PasteAsync(string text, CancellationToken cancellationToken) =>
        IsReady && await paste(text, cancellationToken);

    // A hung or loading window would drop the next key; the call returns once its thread answers.
    public Task WaitUntilResponsiveAsync(CancellationToken cancellationToken) => Task.Run(() =>
        NativeMethods.SendMessageTimeout(target, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _), cancellationToken);

    private static bool Held(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    // One SendInput call, so no physical key lands between the keys. They carry TypeWhisper's marker, so its own
    // shortcut hook ignores the injected keys.
    private bool SendChord(IReadOnlyList<ushort> chord)
    {
        if (chord.Count == 0) return false;
        var keys = new SyntheticKeys(NativeMethods.GetWindowThreadProcessId(target, out _));
        var inputs = chord.Select(keys.Down).Concat(chord.Reverse().Select(keys.Up)).ToArray();
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (sent > 0 && sent < inputs.Length)
        {
            // Release whatever a partial injection left down.
            var release = chord.Reverse().Select(keys.Up).ToArray();
            NativeMethods.SendInput((uint)release.Length, release, Marshal.SizeOf<NativeMethods.Input>());
        }
        return sent == inputs.Length;
    }
}
