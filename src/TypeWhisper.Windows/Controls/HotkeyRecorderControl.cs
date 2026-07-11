using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.Controls;

/// <summary>
/// Provides hotkey recorder control behavior.
/// </summary>
public sealed class HotkeyRecorderControl : Control
{
    private readonly HotkeyRecorderSession _recordingSession = new();
    private static int _activeRecordingControls;
    private Button? _clearButton;

    /// <summary>
    /// Gets the hotkey property.
    /// </summary>
    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(nameof(Hotkey), typeof(string), typeof(HotkeyRecorderControl),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>
    /// Gets whether is recording property.
    /// </summary>
    public static readonly DependencyProperty IsRecordingProperty =
        DependencyProperty.Register(nameof(IsRecording), typeof(bool), typeof(HotkeyRecorderControl),
            new PropertyMetadata(false, OnIsRecordingChanged));

    /// <summary>
    /// Gets the allow modifier only property.
    /// </summary>
    public static readonly DependencyProperty AllowModifierOnlyProperty =
        DependencyProperty.Register(nameof(AllowModifierOnly), typeof(bool), typeof(HotkeyRecorderControl),
            new PropertyMetadata(false));

    /// <summary>
    /// Gets the dependency property that controls whether the add glyph is shown.
    /// </summary>
    public static readonly DependencyProperty UseAddGlyphProperty =
        DependencyProperty.Register(nameof(UseAddGlyph), typeof(bool), typeof(HotkeyRecorderControl),
            new PropertyMetadata(false));

    /// <summary>
    /// Gets the dependency property for the command invoked after a hotkey is recorded.
    /// </summary>
    public static readonly DependencyProperty RecordedCommandProperty =
        DependencyProperty.Register(nameof(RecordedCommand), typeof(ICommand), typeof(HotkeyRecorderControl),
            new PropertyMetadata(null));

    /// <summary>
    /// Gets the dependency property for the recorded-command parameter.
    /// </summary>
    public static readonly DependencyProperty RecordedCommandParameterProperty =
        DependencyProperty.Register(nameof(RecordedCommandParameter), typeof(object), typeof(HotkeyRecorderControl),
            new PropertyMetadata(null));

    /// <summary>
    /// Gets or sets the recorded hotkey text.
    /// </summary>
    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    /// <summary>
    /// Gets whether recording is currently active.
    /// </summary>
    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    /// <summary>
    /// Gets the allow modifier only.
    /// </summary>
    public bool AllowModifierOnly
    {
        get => (bool)GetValue(AllowModifierOnlyProperty);
        set => SetValue(AllowModifierOnlyProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the control should render an add glyph when no hotkey is assigned.
    /// </summary>
    public bool UseAddGlyph
    {
        get => (bool)GetValue(UseAddGlyphProperty);
        set => SetValue(UseAddGlyphProperty, value);
    }

    /// <summary>
    /// Gets or sets the command invoked after recording completes.
    /// </summary>
    public ICommand? RecordedCommand
    {
        get => (ICommand?)GetValue(RecordedCommandProperty);
        set => SetValue(RecordedCommandProperty, value);
    }

    /// <summary>
    /// Gets or sets the parameter passed to the recorded command.
    /// </summary>
    public object? RecordedCommandParameter
    {
        get => GetValue(RecordedCommandParameterProperty);
        set => SetValue(RecordedCommandParameterProperty, value);
    }

    static HotkeyRecorderControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyRecorderControl),
            new FrameworkPropertyMetadata(typeof(HotkeyRecorderControl)));
    }

    /// <summary>
    /// Initializes a new instance of the HotkeyRecorderControl class.
    /// </summary>
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

    /// <summary>
    /// Wires template parts required for clear-button behavior.
    /// </summary>
    public override void OnApplyTemplate()
    {
        if (_clearButton is not null)
            _clearButton.Click -= OnClearButtonClick;

        base.OnApplyTemplate();

        _clearButton = GetTemplateChild("ClearButton") as Button;
        if (_clearButton is not null)
            _clearButton.Click += OnClearButtonClick;
    }

    private void OnClearButtonClick(object sender, RoutedEventArgs e)
    {
        Hotkey = "";
        _recordingSession.Reset();
        IsRecording = false;
        e.Handled = true;
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

    /// <summary>
    /// Starts or cancels recording when the control is clicked and marks the mouse event handled.
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (IsTemplateButtonClick(e.OriginalSource))
            return;

        Focus();

        if (IsRecording)
        {
            CancelRecording();
        }
        else
        {
            BeginRecording();
        }

        e.Handled = true;
    }

    private void BeginRecording()
    {
        _recordingSession.Reset();
        IsRecording = true;
    }

    private bool IsTemplateButtonClick(object originalSource)
    {
        if (originalSource is not DependencyObject source)
            return false;

        return IsDescendantOf(source, _clearButton);
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject? ancestor)
    {
        if (ancestor is null)
            return false;

        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// Cancels an active recording session when keyboard focus leaves the control.
    /// </summary>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsRecording)
            CancelRecording();
    }

    /// <summary>
    /// Captures key presses while recording and updates or clears the bound hotkey value.
    /// </summary>
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
            CommitRecordedHotkey(hotkey);
    }

    /// <summary>
    /// Captures the next mouse button pressed after recording starts.
    /// </summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewMouseDown(e);
            return;
        }

        if (HotkeyKeyMap.TryGetMouseToken(e.ChangedButton, out var token))
        {
            CommitRecordedHotkey(FormatMouseHotkey(_recordingSession.GetCurrentModifiers(), token));
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseDown(e);
    }

    /// <summary>
    /// Completes modifier-only hotkey recording when modifier keys are released.
    /// </summary>
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
            CommitRecordedHotkey(hotkey);

        e.Handled = true;
    }

    private void CommitRecordedHotkey(string hotkey)
    {
        Hotkey = hotkey;
        IsRecording = false;
        _recordingSession.Reset();

        var parameter = RecordedCommandParameter ?? hotkey;
        if (RecordedCommand?.CanExecute(parameter) == true)
            RecordedCommand.Execute(parameter);
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

    internal static string FormatMouseHotkey(ModifierKeys modifiers, string mouseToken)
    {
        var modifierText = FormatModifierOnly(modifiers);
        return modifierText.Length == 0 ? mouseToken : $"{modifierText}+{mouseToken}";
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

    internal static string FormatSingleModifier(Key key) => key switch
    {
        Key.LeftCtrl => "Left Ctrl",
        Key.RightCtrl => "Right Ctrl",
        Key.LeftShift => "Left Shift",
        Key.RightShift => "Right Shift",
        Key.LeftAlt => "Left Alt",
        Key.RightAlt => "Right Alt",
        _ => ""
    };

    internal static string FormatKeyName(Key key) =>
        HotkeyKeyMap.TryGetToken(key, out var token) ? token : "";

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

    /// <summary>
    /// Performs reset.
    /// </summary>
    public void Reset() => _pressedModifiers.Clear();

    /// <summary>
    /// Performs note modifier down.
    /// </summary>
    public void NoteModifierDown(Key key)
    {
        if (HotkeyRecorderControl.IsModifierKey(key))
            _pressedModifiers.Add(key);
    }

    /// <summary>
    /// Performs try record hotkey.
    /// </summary>
    public string TryRecordHotkey(Key key)
    {
        if (HotkeyRecorderControl.IsModifierKey(key))
        {
            NoteModifierDown(key);
            return "";
        }

        return HotkeyRecorderControl.FormatHotkey(GetCurrentModifiers(), key);
    }

    /// <summary>
    /// Performs try record modifier only on release.
    /// </summary>
    public string TryRecordModifierOnlyOnRelease(Key key)
    {
        if (!HotkeyRecorderControl.IsModifierKey(key))
            return "";

        var modifiers = GetCurrentModifiers();
        var hotkey = HotkeyRecorderControl.CountModifiers(modifiers) >= 2
            ? HotkeyRecorderControl.FormatModifierOnly(modifiers)
            : TryFormatSinglePressedModifier();

        _pressedModifiers.Remove(key);
        return hotkey;
    }

    private string TryFormatSinglePressedModifier()
    {
        if (_pressedModifiers.Count != 1)
            return "";

        foreach (var pressedModifier in _pressedModifiers)
            return HotkeyRecorderControl.FormatSingleModifier(pressedModifier);

        return "";
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
