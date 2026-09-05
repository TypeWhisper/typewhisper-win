using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace TypeWhisper.WinUI;

// Local UI capture only: no keyboard hooks, RegisterHotKey or production settings.
public sealed class PrototypeShortcutRecorder : UserControl
{
    private static WeakReference<PrototypeShortcutRecorder>? _active;
    private readonly string _key;
    private readonly Func<string, string?>? _commit;
    private readonly string _defaultValue;
    private readonly Dictionary<string, string> _values;
    private readonly Func<IEnumerable<(string Key, string Label, string Value)>> _bindings;
    private readonly TextBlock _value;
    private readonly TextBlock _hint;
    private readonly HandCursorButton _record;
    private readonly HandCursorButton _apply;
    private readonly StackPanel _editActions;
    private readonly StackPanel _normalActions;
    private readonly PrototypeShortcutWrapPanel _shortcuts = new() { Spacing = 8 };
    private readonly HandCursorButton _add;
    private readonly Border _shell;
    private readonly string _label;
    private int _editingIndex = -1;
    private string _candidate = "";
    private string _heldModifiers = "";
    private readonly HashSet<string> _downModifiers = [];
    private bool _hasMainKey;
    private bool _startingCapture;
    internal bool IsCapturing { get; private set; }
    internal bool IsEditing { get; private set; }

    internal PrototypeShortcutRecorder(string key, string label, string defaultValue, Dictionary<string, string> values,
        Func<IEnumerable<(string Key, string Label, string Value)>> bindings, Func<string, string?>? commit = null)
    {
        _key = key; _label = label; _defaultValue = defaultValue; _values = values; _bindings = bindings;
        _commit = commit;
        var panel = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var bindingsRow = new Grid { ColumnSpacing = 8 };
        bindingsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bindingsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bindingsRow.Children.Add(_shortcuts);
        panel.Children.Add(bindingsRow);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TypeWhisperGlyph { Kind = "keyboard", Width = 18, Height = 18 });
        _value = Text("", 14); Grid.SetColumn(_value, 1); row.Children.Add(_value);
        var action = Text("Record", 12, true); Grid.SetColumn(action, 2); row.Children.Add(action);
        _record = Button(row, "PrototypeSecondaryButtonStyle", $"Record {label} shortcut");
        _record.MinHeight = 44; _record.HorizontalAlignment = HorizontalAlignment.Stretch;
        _record.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _record.Click += (_, _) => Begin(_editingIndex); panel.Children.Add(_record);
        _hint = Text("", 12, true);
        AutomationProperties.SetLiveSetting(_hint, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(_hint);
        _normalActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var reset = Button(new TypeWhisperGlyph
        {
            Kind = "restore", Width = 16, Height = 16,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"]
        }, "PrototypeIconButtonStyle", $"Reset {label} shortcuts");
        ToolTipService.SetToolTip(reset, "Restore defaults");
        reset.MinWidth = 32; reset.Padding = new Thickness(6);
        reset.Click += (_, _) => SetValue(_defaultValue);
        _add = Button(Text("+", 20), "PrototypeSecondaryButtonStyle", $"Add {label} shortcut");
        _add.MinWidth = 34; _add.MinHeight = 34; _add.Padding = new Thickness(6, 0, 6, 0);
        ToolTipService.SetToolTip(_add, "Add another shortcut");
        _add.Click += (_, _) => Begin();
        _normalActions.Children.Add(_add); _normalActions.Children.Add(reset);
        Grid.SetColumn(_normalActions, 1); bindingsRow.Children.Add(_normalActions);
        _editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        _apply = Button("Use shortcut", "PrototypePrimaryButtonStyle", $"Use {label} shortcut");
        _apply.Click += (_, _) => Apply();
        var cancel = Button("Cancel", "PrototypeSecondaryButtonStyle", $"Cancel {label} shortcut capture");
        cancel.Click += (_, _) => Cancel();
        _editActions.Children.Add(_apply); _editActions.Children.Add(cancel); panel.Children.Add(_editActions);
        var (icon, description) = key switch
        {
            "QuickLaunchHotkeys" => ("search", "Open from any app · saved global shortcut"),
            "MainDictationHotkeys" => ("microphone", "Your everyday dictation"),
            "PushToTalkHotkey" => ("run", "Speak while holding"),
            "ToggleOnlyHotkeys" => ("pause", "Press to start or stop"),
            "HoldOnlyHotkeys" => ("keyboard", "Record while held down"),
            "RecentTranscriptionsHotkeys" => ("history", "Open recent transcripts"),
            "CopyLastTranscriptionHotkeys" => ("file", "Copy your latest result"),
            "WorkflowPaletteHotkeys" => ("workflow", "Run a text workflow"),
            _ => ("recorder", "Open the audio recorder")
        };
        var layout = new Grid { ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["ElevatedBrush"],
            Child = new TypeWhisperGlyph { Kind = icon, Width = 19, Height = 19 }
        });
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var heading = Text(label, 14); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        copy.Children.Add(heading); copy.Children.Add(Text(description, 12, true));
        Grid.SetColumn(copy, 1); layout.Children.Add(copy);
        Grid.SetColumn(panel, 2); layout.Children.Add(panel);
        _shell = new Border
        {
            Child = layout, Padding = new Thickness(12, 14, 12, 14), CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(1)
        };
        Content = _shell;
        SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 640;
            Grid.SetRow(panel, narrow ? 1 : 0); Grid.SetColumn(panel, narrow ? 1 : 2);
            Grid.SetColumnSpan(panel, narrow ? 2 : 1); Grid.SetColumnSpan(copy, narrow ? 2 : 1);
            panel.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        };
        Unloaded += (_, _) => Cancel(false);
        LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsEditing || _startingCapture || XamlRoot is null) return;
            for (var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
                if (ReferenceEquals(focused, this)) return;
            Cancel(false);
        });
        Refresh();
    }

    private static TextBlock Text(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };
    private static HandCursorButton Button(object content, string style, string name)
    {
        var button = new HandCursorButton { Content = content, Style = (Style)Application.Current.Resources[style] };
        AutomationProperties.SetName(button, name); return button;
    }
    private string[] Current => PrototypeShortcutRules.Split(_values.GetValueOrDefault(_key, _defaultValue));
    private void Refresh()
    {
        _shortcuts.Children.Clear();
        var current = Current;
        if (current.Length == 0)
        {
            var empty = Button(Text("Set shortcut", 12, true), "PrototypeIconButtonStyle", $"Set {_label} shortcut");
            empty.HorizontalAlignment = HorizontalAlignment.Stretch;
            empty.HorizontalContentAlignment = HorizontalAlignment.Right;
            empty.Click += (_, _) => Begin();
            _shortcuts.Children.Add(empty);
        }
        for (var i = 0; i < current.Length; i++)
        {
            var index = i;
            var chord = current[i];
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.HorizontalAlignment = HorizontalAlignment.Right;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var keycap in chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                content.Children.Add(new Border
                {
                    Background = (Brush)Application.Current.Resources["ElevatedBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 4, 7, 4), MinWidth = 28,
                    Child = Text(keycap, 12)
                });
            var edit = Button(content, "PrototypeIconButtonStyle", $"Edit {_label} shortcut {chord}");
            edit.MinHeight = 34; edit.Padding = new Thickness(2);
            edit.HorizontalAlignment = HorizontalAlignment.Right;
            edit.HorizontalContentAlignment = HorizontalAlignment.Left;
            edit.Click += (_, _) => Begin(index);
            ToolTipService.SetToolTip(edit, "Change this shortcut");
            row.Children.Add(edit);
            var remove = Button("×", "PrototypeDestructiveButtonStyle", $"Remove {_label} shortcut {chord}");
            remove.MinWidth = 28; remove.MinHeight = 28; remove.Padding = new Thickness(4, 0, 4, 0);
            ToolTipService.SetToolTip(remove, "Remove this shortcut");
            remove.Click += (_, _) =>
            {
                SetValue(PrototypeShortcutRules.RemoveAt(Current, index));
                _add.Focus(FocusState.Keyboard);
            };
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            _shortcuts.Children.Add(row);
        }
        _record.Visibility = Visibility.Collapsed;
        _hint.Text = ""; _hint.Visibility = Visibility.Collapsed;
        _shell.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _record.Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"];
    }
    private void Begin(int index = -1)
    {
        if (_active?.TryGetTarget(out var previous) == true && !ReferenceEquals(previous, this)) previous.Cancel(false);
        _active = new(this);
        _editingIndex = index;
        _startingCapture = true;
        IsEditing = IsCapturing = true; _candidate = _heldModifiers = ""; _hasMainKey = false;
        _downModifiers.Clear();
        _value.Text = "Press shortcut…";
        _record.Visibility = Visibility.Visible;
        _hint.Visibility = Visibility.Visible;
        _shell.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
        _shortcuts.IsHitTestVisible = false;
        foreach (var control in _shortcuts.Children.OfType<Grid>().SelectMany(row => row.Children).OfType<Control>().Concat(_shortcuts.Children.OfType<Control>())) control.IsEnabled = false;
        _hint.Text = "Press keys, then choose Use shortcut. Esc cancels; Tab moves to the buttons.";
        _apply.IsEnabled = false;
        _normalActions.Visibility = Visibility.Collapsed; _editActions.Visibility = Visibility.Visible;
        _record.Style = (Style)Application.Current.Resources["PrototypePrimaryButtonStyle"];
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsEditing)
            {
                _record.UpdateLayout();
                _record.Focus(FocusState.Programmatic);
            }
            _startingCapture = false;
        });
    }
    internal void Cancel(bool focus = true)
    {
        if (!IsEditing) return;
        IsEditing = IsCapturing = false;
        _shortcuts.IsHitTestVisible = true;
        foreach (var control in _shortcuts.Children.OfType<Grid>().SelectMany(row => row.Children).OfType<Control>().Concat(_shortcuts.Children.OfType<Control>())) control.IsEnabled = true;
        _normalActions.Visibility = Visibility.Visible; _editActions.Visibility = Visibility.Collapsed;
        Refresh(); if (focus) _add.Focus(FocusState.Keyboard);
    }
    private void SetValue(string value)
    {
        var conflict = PrototypeShortcutRules.Split(value).Select(chord => PrototypeShortcutRules.Conflict(chord, _key, _bindings())).FirstOrDefault(error => error is not null);
        if (conflict is not null) { _hint.Text = conflict; _hint.Visibility = Visibility.Visible; return; }
        var commitError = _commit?.Invoke(value);
        if (commitError is not null) { _hint.Text = commitError; _hint.Visibility = Visibility.Visible; return; }
        _values[_key] = value; Cancel(false); Refresh();
    }
    private void Apply()
    {
        if (_candidate.Length == 0 || Validate(_candidate) is not null) return;
        SetValue(PrototypeShortcutRules.Upsert(Current, _editingIndex, _candidate)); _add.Focus(FocusState.Keyboard);
    }
    // Match the regular Windows shortcut page: every action accepts modifier-only chords.
    private string? Validate(string candidate) => PrototypeShortcutRules.Validate(candidate, allowModifiersOnly: true)
        ?? PrototypeShortcutRules.Duplicate(candidate, Current, _editingIndex)
        ?? PrototypeShortcutRules.Conflict(candidate, _key, _bindings());
    private void Candidate(string candidate)
    {
        _candidate = candidate;
        var error = Validate(candidate);
        _value.Text = candidate.Replace("+", " + ");
        _hint.Text = error ?? "Ready to use. Choose Use shortcut or press Enter to confirm.";
        _apply.IsEnabled = error is null;
    }
    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    private static string Modifiers() => string.Join("+", new[]
    {
        Down(VirtualKey.Control) ? "Ctrl" : "", Down(VirtualKey.Menu) ? "Alt" : "", Down(VirtualKey.Shift) ? "Shift" : "",
        Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? "Win" : ""
    }.Where(value => value.Length > 0));
    private static bool Modifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;
    private static string ModifierName(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "Ctrl",
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "Alt",
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "Shift",
        _ => "Win"
    };
    internal void CaptureKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { Cancel(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Tab) { IsCapturing = false; return; }
        e.Handled = true;
        var modifiers = Modifiers();
        if (e.Key == VirtualKey.Enter && modifiers.Length == 0 && _apply.IsEnabled) { Apply(); return; }
        if (Modifier(e.Key))
        {
            if (!_hasMainKey)
            {
                // The routed event can precede the thread's modifier-state update.
                // Include its key explicitly and retain the full chord through release.
                var pressed = ModifierName(e.Key);
                if (_downModifiers.Count == 0) { _heldModifiers = ""; _candidate = ""; _apply.IsEnabled = false; }
                _downModifiers.Add(pressed);
                _heldModifiers = string.Join("+", (_heldModifiers + "+" + modifiers + "+" + pressed)
                    .Split('+', StringSplitOptions.RemoveEmptyEntries).Distinct());
                _value.Text = _heldModifiers.Replace("+", " + ") + " + …";
            }
            return;
        }
        _hasMainKey = true;
        var key = (int)e.Key;
        var name = key is >= 65 and <= 90 ? ((char)key).ToString()
            : key is >= 48 and <= 57 ? ((char)key).ToString()
            : key is >= 112 and <= 135 ? $"F{key - 111}"
            : key is >= 96 and <= 105 ? $"Num{key - 96}"
            : e.Key switch { VirtualKey.Menu => "Alt", VirtualKey.Enter => "Enter", VirtualKey.Back => "Backspace", _ => e.Key.ToString() };
        Candidate(modifiers.Length == 0 ? name : $"{modifiers}+{name}");
    }
    internal void CaptureKeyUp(KeyRoutedEventArgs e)
    {
        if (!IsCapturing) return;
        if (Modifier(e.Key))
        {
            _downModifiers.Remove(ModifierName(e.Key));
            if (!_hasMainKey && _heldModifiers.Length > 0)
            {
                // Some native modifier combinations omit a routed key-down but
                // still deliver key-up. Preserve that released key in the chord.
                _heldModifiers = string.Join("+", (_heldModifiers + "+" + ModifierName(e.Key))
                    .Split('+', StringSplitOptions.RemoveEmptyEntries).Distinct());
                Candidate(_heldModifiers);
            }
        }
        e.Handled = true;
    }
}
