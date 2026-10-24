using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed class DictationHotkeyRegistration : IShortcutRegistrationBackend, IMouseShortcutHandler, IDisposable
{
    private readonly HotkeyRegistration _regular;
    private readonly NativeMethods.HookProc _callback;
    private IntPtr _hook;
    private bool _interrupted;
    private HashSet<string> _bindings = [];
    private HybridHotkeyState _state = new();
    private bool _disposed;
    // The mouse hook thread decides which clicks a shortcut owns; the gesture itself runs on the UI thread.
    private readonly object _mouseLock = new();
    private readonly MouseShortcutPresses _mousePresses = new();
    private readonly Func<bool>? _paused;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
    private readonly Action<int, bool, uint, string?, bool> _mouseGesture;
    // Mouse presses that a shortcut owns never reach Windows' key state, so held buttons are tracked here (UI thread).
    private readonly HashSet<int> _mouseDown = [];
    private HashSet<int> _mouseButtons = [];
    private bool _mouseHooked;
    // When each modifier was last released, so a replayed release keeps its real time (UI thread).
    private readonly Dictionary<int, uint> _modifierReleased = [];
    // Modifiers replayed with a click's press, released again around the click's release (UI thread).
    private readonly Dictionary<int, (int[] Modifiers, uint Press)> _replayed = [];
    public string Value { get; private set; } = "";
    // Lets the shortcut recorder tell AltGr from a real Ctrl, which XAML key events cannot.
    internal static bool AltGrControlDown { get; private set; }
    internal DictationHotkeyRegistration(Microsoft.UI.Xaml.Window window, Action<HybridHotkeyAction> invoke, Func<bool> isRecording,
        Func<RecordingMode>? recordingMode = null, Func<bool>? paused = null, int idBase = 0x6500)
    {
        recordingMode ??= () => RecordingMode.Hybrid;
        _paused = paused;
        _queue = window.DispatcherQueue;
        // Reserve ordinary chords, but use the hook for both press and release.
        _regular = new(window, () => { }, idBase);
        void Dispatch(HybridHotkeyAction? action)
        {
            // The hook runs on the installing UI thread. The receiver only captures
            // intent here and schedules audio work through its bounded coordinator.
            // This preserves event-time readiness before a long UI queue can drain.
            if (action is not null && !_disposed && !ShortcutRecorder.AnyEditing) invoke(action.Value);
        }
        bool Held(int held) => ShortcutKeys.IsMouseButton(held) ? _mouseDown.Contains(held)
            : (NativeMethods.GetAsyncKeyState(held == HybridHotkeyState.AltGrControl ? 0xA2 : held) & 0x8000) != 0;
        void Feed(int key, bool down, uint time, Func<int, bool>? held = null)
        {
            var mode = recordingMode();
            var now = Environment.TickCount64;
            _state.LostReleaseMilliseconds = HybridHotkeyState.LostReleaseAfter(KeyboardRepeatTiming.LongestGapMilliseconds);
            Dispatch(_state.Key(key, down, now - InputEventTiming.Age(time, now), _bindings, isRecording(), mode, paused?.Invoke() == true,
                held ?? Held, stale: InputEventTiming.IsStale(time, now)));
            LogDroppedKeys(_state.DroppedKeys, time);
        }
        _mouseGesture = (key, down, time, chord, editing) =>
        {
            if (_disposed || _interrupted) return;
            if (editing)
            {
                // Like a reserved keyboard chord, the press goes to the open shortcut editor instead of dictation.
                _state = new(); _mouseDown.Clear(); _replayed.Clear();
                if (chord is not null) ShortcutRecorder.CaptureRegisteredShortcut(chord);
                return;
            }
            // Mouse buttons don't repeat: a second press without a release in between follows a lost release.
            if (down && _mouseDown.Contains(key)) ReleaseClick(key, time);
            if (!down) { ReleaseClick(key, time); return; }
            // A modifier of the matched chord that was released before this queued click is replayed as pressed,
            // so the gesture sees the order Windows saw. Its release follows around the click's release.
            int[] replay = MouseShortcutPresses.ReleasedModifiers(chord, held => (NativeMethods.GetAsyncKeyState(held) & 0x8000) != 0);
            bool HeldWithReplay(int held) => replay.Contains(held) || Held(held);
            foreach (var modifier in replay) Feed(modifier, true, time, HeldWithReplay);
            Feed(key, true, time, HeldWithReplay);
            _mouseDown.Add(key);
            if (replay.Length == 0) return;
            // While the hook still sees the button held, every release already observed came before the click's
            // release, so it is replayed now. Only after the button was released do they wait for the click's release.
            bool stillHeld;
            lock (_mouseLock) stillHeld = _mousePresses.Holds(key);
            if (stillHeld)
            {
                var waiting = replay.ToHashSet();
                bool HeldWaiting(int held) => waiting.Contains(held) || Held(held);
                foreach (var (modifier, released) in MouseShortcutPresses.KnownReleases(replay, time,
                    modifier => _modifierReleased.TryGetValue(modifier, out var at) ? at : null))
                {
                    Feed(modifier, false, released, HeldWaiting);
                    waiting.Remove(modifier);
                }
                replay = replay.Where(waiting.Contains).ToArray();
            }
            if (replay.Length > 0) _replayed[key] = (replay, time);
        };
        // Like Windows' key state seen from a keyboard hook, a button or modifier counts as held until its release is handled.
        void ReleaseClick(int key, uint time)
        {
            int[] modifiers = [];
            var press = time;
            if (_replayed.Remove(key, out var replayed)) (modifiers, press) = replayed;
            var waiting = modifiers.ToHashSet();
            bool HeldWaiting(int held) => waiting.Contains(held) || Held(held);
            var plan = MouseShortcutPresses.PlanReleases(modifiers, press, time,
                modifier => _modifierReleased.TryGetValue(modifier, out var released) ? released : null);
            foreach (var step in plan.Where(step => step.BeforeClick)) { Feed(step.Modifier, false, step.Time, HeldWaiting); waiting.Remove(step.Modifier); }
            Feed(key, false, time, HeldWaiting);
            _mouseDown.Remove(key);
            foreach (var step in plan.Where(step => !step.BeforeClick)) { Feed(step.Modifier, false, step.Time, HeldWaiting); waiting.Remove(step.Modifier); }
        }
        _callback = (code, message, data) =>
        {
            if (code >= 0 && !_interrupted && !_disposed)
            {
                var key = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
                var altGr = ShortcutKeys.IsAltGrControl(key.Key, key.Scan);
                var pressed = message.ToInt64() is 0x100 or 0x104;
                // A real left Ctrl or the right Alt release also ends AltGr, in case its Ctrl release was missed.
                if (key.Key == 0xA2 || key.Key == 0xA5 && !pressed) AltGrControlDown = altGr && pressed;
                // Keys injected by other tools count like physical ones; only our own paste, copy and media keys are skipped.
                if (!OwnKeyboardInput.Sent(key.Flags, key.Extra))
                {
                    var down = message.ToInt64() is 0x100 or 0x104;
                    var up = message.ToInt64() is 0x101 or 0x105;
                    if (up && MouseShortcutPresses.ReplayKey((int)key.Key) is var replayKey and not 0) _modifierReleased[replayKey] = key.Time;
                    if (ShortcutRecorder.AnyEditing) _state = new();
                    else if (down || up) Feed(altGr ? HybridHotkeyState.AltGrControl : (int)key.Key, down, key.Time);
                }
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { _regular.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    // Every shortcut hook sees the same event, so each lost key-up is logged once per event and key.
    // All hooks run on the UI thread.
    private static uint _droppedEvent;
    private static readonly HashSet<int> _droppedLogged = [];
    private static void LogDroppedKeys(IReadOnlyList<int> dropped, uint eventTime)
    {
        if (dropped.Count == 0) return;
        if (eventTime != _droppedEvent) { _droppedEvent = eventTime; _droppedLogged.Clear(); }
        foreach (var key in dropped.Where(_droppedLogged.Add))
            AppDiagnostics.Write("shortcut.lost-release kind=" + ShortcutKeys.DiagnosticKind(key));
    }

    // Runs on the mouse hook thread: only the decision whether the click belongs to a shortcut.
    bool IMouseShortcutHandler.Button(int key, bool down, uint time, string modifiers)
    {
        if (_disposed || _interrupted || !_mouseButtons.Contains(key)) return false;
        var editing = ShortcutRecorder.AnyEditing;
        bool owned;
        string? chord;
        lock (_mouseLock) owned = _mousePresses.Button(key, down, modifiers, _bindings, editing || _paused?.Invoke() != true, out chord);
        // Only clicks that a shortcut owns reach the gesture. A click left with the focused app must not
        // start dictation later, e.g. when its extra modifier was released before the UI thread handles it.
        if (owned) _queue.TryEnqueue(() => _mouseGesture(key, down, time, chord, editing));
        return owned;
    }

    void IMouseShortcutHandler.Reset()
    {
        lock (_mouseLock) _mousePresses.Reset();
    }

    internal void ObservePause() => _state.Suspend();
    // Read by the Enter hook, which runs on the same UI thread.
    internal bool HoldsKey(int key) => !_disposed && !_interrupted && _state.Holds(key);

    internal void Interrupt()
    {
        _interrupted = true;
        AltGrControlDown = false;
        _state.ResetAfterInterruption([]);
        _mouseDown.Clear(); _replayed.Clear(); ((IMouseShortcutHandler)this).Reset();
    }

    internal string? Recover()
    {
        if (_disposed) return null;
        var replacement = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (replacement == IntPtr.Zero)
            return Loc.T("Could not restore dictation keyboard hook (Windows error {0}). Retry after unlocking, or restart TypeWhisper.", Marshal.GetLastWin32Error());
        var previous = _hook;
        _hook = replacement;
        if (previous != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(previous);
        // Generic modifier VKs alias the physical left/right keys. Seeding both
        // would leave a phantom generic key down after the physical key-up.
        _state.ResetAfterInterruption(Enumerable.Range(8, 247)
            .Where(key => key is not (0x10 or 0x11 or 0x12) && (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0));
        _mouseDown.Clear(); _replayed.Clear(); ((IMouseShortcutHandler)this).Reset();
        _interrupted = false;
        return null;
    }

    public string? TryChange(string value)
    {
        var chords = ShortcutRules.Split(value).Select(ShortcutRules.Normalize).Distinct().ToArray();
        foreach (var chord in chords)
            if (ShortcutRules.Validate(chord, true) is string error) return error;
        static bool ModifierOnly(string chord) => chord.Split('+').All(p => p is "CTRL" or "ALT" or "SHIFT" or "WIN");
        var mouseButtons = chords.Where(ShortcutKeys.IsMouseChord)
            .Select(chord => ShortcutKeys.TryParse(chord.Split('+')[^1], out var button) ? button : 0).ToHashSet();
        // Install the mouse hook first, so a failure leaves the previous shortcuts untouched.
        var hookAdded = false;
        if (mouseButtons.Count > 0 && !_mouseHooked)
        {
            if (MouseShortcutHook.Add(this) is { } hookError) return hookError;
            _mouseHooked = hookAdded = true;
        }
        var registrationError = _regular.TryChange(string.Join(",", chords.Where(c => !ModifierOnly(c) && !ShortcutKeys.IsMouseChord(c))));
        if (registrationError is not null)
        {
            if (hookAdded) StopMouse();
            return registrationError;
        }
        if (mouseButtons.Count == 0) StopMouse();
        _bindings = chords.ToHashSet();
        _mouseButtons = mouseButtons;
        _state = new();
        _mouseDown.Clear(); _replayed.Clear(); ((IMouseShortcutHandler)this).Reset();
        Value = string.Join(",", chords);
        return null;
    }
    private void StopMouse()
    {
        if (!_mouseHooked) return;
        MouseShortcutHook.Remove(this);
        _mouseHooked = false;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero;
        StopMouse();
        _regular.Dispose();
    }
}
