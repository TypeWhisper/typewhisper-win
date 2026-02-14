using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TypeWhisper.Windows.Controls;

public sealed class HotkeyRecorderControl : Control
{
    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(nameof(Hotkey), typeof(string), typeof(HotkeyRecorderControl),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsRecordingProperty =
        DependencyProperty.Register(nameof(IsRecording), typeof(bool), typeof(HotkeyRecorderControl),
            new PropertyMetadata(false));

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

        if (e.Key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore standalone modifier presses — wait for the full combo
        if (IsModifierKey(key))
            return;

        var hotkey = FormatHotkey(Keyboard.Modifiers, key);
        if (!string.IsNullOrEmpty(hotkey))
        {
            Hotkey = hotkey;
            IsRecording = false;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsRecording || !AllowModifierOnly)
        {
            base.OnPreviewKeyUp(e);
            return;
        }

        // For modifier-only mode: if a modifier was released and we still have modifiers held,
        // check if the released key completes a modifier combo
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifierKey(key))
            return;

        // Build the modifier combo from what WAS pressed (including the just-released key)
        var mods = Keyboard.Modifiers;
        // Add back the just-released modifier
        mods |= key switch
        {
            Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
            Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
            Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
            Key.LWin or Key.RWin => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };

        // Need at least 2 modifiers for a modifier-only combo
        var count = CountModifiers(mods);
        if (count >= 2)
        {
            var hotkey = FormatModifierOnly(mods);
            if (!string.IsNullOrEmpty(hotkey))
            {
                Hotkey = hotkey;
                IsRecording = false;
                e.Handled = true;
            }
        }
    }

    private void CancelRecording()
    {
        IsRecording = false;
    }

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
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

    private static string FormatModifierOnly(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    private static string FormatKeyName(Key key) => key switch
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

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin;

    private static int CountModifiers(ModifierKeys mods)
    {
        var count = 0;
        if (mods.HasFlag(ModifierKeys.Control)) count++;
        if (mods.HasFlag(ModifierKeys.Shift)) count++;
        if (mods.HasFlag(ModifierKeys.Alt)) count++;
        if (mods.HasFlag(ModifierKeys.Windows)) count++;
        return count;
    }
}
