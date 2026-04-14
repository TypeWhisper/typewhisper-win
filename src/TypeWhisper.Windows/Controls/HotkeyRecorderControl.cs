using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.Controls;

public sealed class HotkeyRecorderControl : Control
{
    private readonly HotkeyRecorderSession _recordingSession = new();
    private static int _activeRecordingControls;

    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(nameof(Hotkey), typeof(string), typeof(HotkeyRecorderControl),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsRecordingProperty =
        DependencyProperty.Register(nameof(IsRecording), typeof(bool), typeof(HotkeyRecorderControl),
            new PropertyMetadata(false, OnIsRecordingChanged));

    public static readonly DependencyProperty AllowModifierOnlyProperty =
        DependencyProperty.Register(nameof(AllowModifierOnly), typeof(bool), typeof(HotkeyRecorderControl),
            new PropertyMetadata(false));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    public bool AllowModifierOnly
    {
        get => (bool)GetValue(AllowModifierOnlyProperty);
        set => SetValue(AllowModifierOnlyProperty, value);
    }

    static HotkeyRecorderControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyRecorderControl),
            new FrameworkPropertyMetadata(typeof(HotkeyRecorderControl)));
    }

    public HotkeyRecorderControl()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        Unloaded += (_, _) =>
        {
            if (IsRecording)
                CancelRecording();
        };
    }

    private static void OnIsRecordingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HotkeyRecorderControl)
            return;

        var isRecording = e.NewValue is true;
        if (isRecording)
        {
            _activeRecordingControls++;
        }
        else if (_activeRecordingControls > 0)
        {
            _activeRecordingControls--;
        }

        var hotkeyService = App.Services.GetRequiredService<HotkeyService>();
        hotkeyService.IsEnabled = _activeRecordingControls == 0;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (IsRecording)
        {
            CancelRecording();
        }
        else
        {
            _recordingSession.Reset();
            IsRecording = true;
        }

        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsRecording)
            CancelRecording();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        // Escape, Delete, Backspace all clear the hotkey
        if (e.Key is Key.Escape or Key.Delete or Key.Back)
        {
            Hotkey = "";
            _recordingSession.Reset();
            IsRecording = false;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (IsModifierKey(key))
        {
            _recordingSession.NoteModifierDown(key);
            return;
        }

        var hotkey = _recordingSession.TryRecordHotkey(key);
        if (!string.IsNullOrEmpty(hotkey))
        {
            Hotkey = hotkey;
            IsRecording = false;
            _recordingSession.Reset();
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsRecording || !AllowModifierOnly)
        {
            base.OnPreviewKeyUp(e);
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifierKey(key))
            return;

        var hotkey = AllowModifierOnly
            ? _recordingSession.TryRecordModifierOnlyOnRelease(key)
            : "";

        if (!string.IsNullOrEmpty(hotkey))
        {
            Hotkey = hotkey;
            IsRecording = false;
            _recordingSession.Reset();
        }

        e.Handled = true;
    }

    private void CancelRecording()
    {
        _recordingSession.Reset();
        IsRecording = false;
    }

    internal static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var keyName = FormatKeyName(key);
        if (string.IsNullOrEmpty(keyName))
            return "";

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    internal static string FormatModifierOnly(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    internal static string FormatKeyName(Key key) => key switch
    {
        Key.Space => "Space",
        >= Key.F1 and <= Key.F12 => key.ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemBackslash or Key.Oem5 => "\\",
        Key.OemTilde => "`",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.PrintScreen or Key.Snapshot => "PrintScreen",
        Key.Pause => "Pause",
        Key.Scroll => "ScrollLock",
        _ => ""
    };

    internal static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin;

    internal static int CountModifiers(ModifierKeys mods)
    {
        var count = 0;
        if (mods.HasFlag(ModifierKeys.Control)) count++;
        if (mods.HasFlag(ModifierKeys.Shift)) count++;
        if (mods.HasFlag(ModifierKeys.Alt)) count++;
        if (mods.HasFlag(ModifierKeys.Windows)) count++;
        return count;
    }
}

internal sealed class HotkeyRecorderSession
{
    private readonly HashSet<Key> _pressedModifiers = [];

    public void Reset() => _pressedModifiers.Clear();

    public void NoteModifierDown(Key key)
    {
        if (HotkeyRecorderControl.IsModifierKey(key))
            _pressedModifiers.Add(key);
    }

    public string TryRecordHotkey(Key key)
    {
        if (HotkeyRecorderControl.IsModifierKey(key))
        {
            NoteModifierDown(key);
            return "";
        }

        return HotkeyRecorderControl.FormatHotkey(GetCurrentModifiers(), key);
    }

    public string TryRecordModifierOnlyOnRelease(Key key)
    {
        if (!HotkeyRecorderControl.IsModifierKey(key))
            return "";

        var modifiers = GetCurrentModifiers();
        var hotkey = HotkeyRecorderControl.CountModifiers(modifiers) >= 2
            ? HotkeyRecorderControl.FormatModifierOnly(modifiers)
            : "";

        _pressedModifiers.Remove(key);
        return hotkey;
    }

    internal ModifierKeys GetCurrentModifiers()
    {
        var modifiers = ModifierKeys.None;

        foreach (var key in _pressedModifiers)
        {
            modifiers |= key switch
            {
                Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
                Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
                Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
                Key.LWin or Key.RWin => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
        }

        return modifiers;
    }
}
