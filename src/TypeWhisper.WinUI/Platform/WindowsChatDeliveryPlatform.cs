using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

/// <summary>Delivers game chat messages to one window. Pasting is delegated so the caller owns the clipboard.</summary>
internal sealed class WindowsChatDeliveryPlatform(IntPtr target, Func<string, Task<bool>> paste, int settleMilliseconds) : IChatDeliveryPlatform
{
    private const uint WM_NULL = 0x0000, SMTO_ABORTIFHUNG = 0x0002;
    private static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    private readonly int _settle = Math.Clamp(settleMilliseconds, 0, 1000);

    public bool TargetIsReady => target != IntPtr.Zero && NativeMethods.GetForegroundWindow() == target
        && !Modifiers.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);

    public bool Press(IReadOnlyList<ushort> chord) => SendChord(target, chord);

    /// <summary>Presses a game add-on's signal chord while the dictation's shortcut may still be held. A Ctrl or Shift
    /// the user holds is left alone: releasing it would end a push-to-talk shortcut. Alt or Windows held would turn the
    /// chord into another one, so nothing is sent then.</summary>
    internal static bool Signal(IntPtr target, IReadOnlyList<ushort> chord)
    {
        if (target == IntPtr.Zero || NativeMethods.GetForegroundWindow() != target || new[] { 0x12, 0x5B, 0x5C }.Any(Held)) return false;
        var missing = chord.Where(key => !Held(key)).ToArray();
        return missing.Length > 0 && SendChord(target, missing);
    }

    private static bool Held(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    // One SendInput call, so no physical key lands between the keys. They carry TypeWhisper's marker, so its own
    // shortcut hook ignores the injected Ctrl and Shift.
    private static bool SendChord(IntPtr target, IReadOnlyList<ushort> chord)
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

    public Task<bool> InsertAsync(string text) => paste(text);

    public async Task SettleAsync()
    {
        // A hung or loading game would drop the next key; wait until its window thread answers, then give it a few frames.
        await Task.Run(() => NativeMethods.SendMessageTimeout(target, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _));
        if (_settle > 0) await Task.Delay(_settle);
    }

    public Task PauseAsync(int milliseconds) => Task.Delay(Math.Clamp(milliseconds, 0, 5000));
}
