using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.System;
using global::Windows.UI.Core;

namespace TypeWhisper.WinUI;

// Local UI capture only: no keyboard hooks, RegisterHotKey or production settings.
public sealed class ShortcutRecorder : UserControl
{
    private static WeakReference<ShortcutRecorder>? _active;
    internal static bool AnyEditing => _active?.TryGetTarget(out var recorder) == true && recorder.IsEditing;
    // RegisterHotKey consumes the main key before XAML can capture it. Keep the
    // reservation and route its actual chord to the open editor instead of running it.
    internal static bool CaptureRegisteredShortcut(string chord)
    {
        if (_active is null || !_active.TryGetTarget(out var recorder) || !recorder.IsEditing || !recorder.IsLoaded) return false;
        if (recorder.IsCapturing)
        {
            recorder._hasMainKey = true;
            recorder.Candidate(chord);
        }
        return true;
    }
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
    private readonly ShortcutWrapPanel _shortcuts = new() { Spacing = 8 };
    private readonly HandCursorButton _add;
    private readonly Border _shell;
    private readonly string _label;
    private int _editingIndex = -1;
    private string _candidate = "";
    private string _heldModifiers = "";
    private readonly HashSet<string> _downModifiers = [];
    private bool _hasMainKey;
    private bool _altGr;
    private bool _startingCapture;
    private bool _commitRefreshQueued;
    // The shortcut test, offered only where the shortcut is already live.
    private static readonly string[] PausableKeys = ["MainDictationHotkeys", "PushToTalkHotkey", "ToggleOnlyHotkeys", "HoldOnlyHotkeys"];
    private readonly HandCursorButton? _test;
    private readonly HandCursorButton _repair;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _testTimer;
    private ShortcutTestSession? _testSession;
    // Keys still held when the test ends must not press the Test button through key repeat.
    private bool _testKeysHeld;
    internal bool IsCapturing { get; private set; }
    internal bool IsEditing { get; private set; }

    internal ShortcutRecorder(string key, string label, string defaultValue, Dictionary<string, string> values,
        Func<IEnumerable<(string Key, string Label, string Value)>> bindings, Func<string, string?>? commit = null, bool testable = false)
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
        var action = Text(Loc.T("Record"), 12, true); Grid.SetColumn(action, 2); row.Children.Add(action);
        _record = Button(row, "SecondaryButtonStyle", Loc.T("Record {0} shortcut", label));
        _record.MinHeight = 44; _record.HorizontalAlignment = HorizontalAlignment.Stretch;
        _record.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _record.Click += (_, _) => Begin(_editingIndex); panel.Children.Add(_record);
        _hint = Text("", 12, true);
        AutomationProperties.SetLiveSetting(_hint, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(_hint);
        _repair = Button(Loc.T("Repair"), "SecondaryButtonStyle", Loc.T("Restart the shortcut listener"));
        _repair.HorizontalAlignment = HorizontalAlignment.Left; _repair.Visibility = Visibility.Collapsed;
        _repair.Click += (_, _) => Repair();
        panel.Children.Add(_repair);
        _normalActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var reset = Button(new TypeWhisperGlyph
        {
            Kind = "restore", Width = 16, Height = 16,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"]
        }, "IconButtonStyle", Loc.T("Reset {0} shortcuts", label));
        ToolTipService.SetToolTip(reset, Loc.T("Restore defaults"));
        reset.MinWidth = 32; reset.Padding = new Thickness(6);
        reset.Click += (_, _) => SetValue(_defaultValue);
        _add = Button(Text("+", 20), "SecondaryButtonStyle", Loc.T("Add {0} shortcut", label));
        _add.MinWidth = 34; _add.MinHeight = 34; _add.Padding = new Thickness(6, 0, 6, 0);
        ToolTipService.SetToolTip(_add, Loc.T("Add another shortcut"));
        _add.Click += (_, _) => Begin();
        _normalActions.Children.Add(_add);
        if (testable)
        {
            _test = Button(Text(Loc.T("Test"), 12), "SecondaryButtonStyle", Loc.T("Test {0} shortcut", label));
            _test.MinHeight = 34; _test.Padding = new Thickness(10, 0, 10, 0);
            ToolTipService.SetToolTip(_test, Loc.T("Check whether this shortcut reaches TypeWhisper"));
            _test.Click += (_, _) => { if (_testSession is null) StartTest(); else StopTest(Loc.T("Test stopped.")); };
            _normalActions.Children.Add(_test);
            // The keys of the shortcut must not press the focused Test button while the test listens; Esc stops it.
            PreviewKeyDown += (_, e) =>
            {
                if (_testSession is null)
                {
                    // A fresh press after the test is ordinary input again; repeats of a held shortcut are not.
                    if (_testKeysHeld && e.KeyStatus.WasKeyDown) e.Handled = true; else _testKeysHeld = false;
                    return;
                }
                e.Handled = true;
                if (e.Key == VirtualKey.Escape) StopTest(Loc.T("Test stopped."));
            };
            PreviewKeyUp += (_, e) => { if (_testSession is not null || _testKeysHeld) e.Handled = true; };
        }
        _normalActions.Children.Add(reset);
        Grid.SetColumn(_normalActions, 1); bindingsRow.Children.Add(_normalActions);
        _editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        _apply = Button(Loc.T("Use shortcut"), "PrimaryButtonStyle", Loc.T("Use {0} shortcut", label));
        _apply.Click += (_, _) => Apply();
        var cancel = Button(Loc.T("Cancel"), "SecondaryButtonStyle", Loc.T("Cancel {0} shortcut capture", label));
        cancel.Click += (_, _) => Cancel();
        _editActions.Children.Add(_apply); _editActions.Children.Add(cancel); panel.Children.Add(_editActions);
        var (icon, description) = key switch
        {
            "MainDictationHotkeys" => ("microphone", Loc.T("Your everyday dictation")),
            "CancelProcessingHotkeys" => ("keyboard", Loc.T("Cancel final dictation processing or an active selected-text workflow")),
            "PushToTalkHotkey" => ("run", Loc.T("Speak while holding")),
            "ToggleOnlyHotkeys" => ("pause", Loc.T("Press to start or stop")),
            "HoldOnlyHotkeys" => ("keyboard", Loc.T("Record while held down")),
            "RecentTranscriptionsHotkeys" => ("history", Loc.T("Open recent transcripts")),
            "CopyLastTranscriptionHotkeys" => ("file", Loc.T("Copy the last dictation from this session")),
            "PasteLastTranscriptionHotkeys" => ("text", Loc.T("Insert the last dictation from this session into the focused app")),
            "ReadLastTranscriptionHotkeys" => ("audio", Loc.T("Read last dictation aloud; press again to stop")),
            "WorkflowPaletteHotkeys" => ("workflow", Loc.T("Run a text workflow")),
            "WorkflowSelectedTextHotkeys" => ("workflow", Loc.T("Activate this workflow · saved when you save the workflow")),
            _ => ("recorder", Loc.T("Open the audio recorder"))
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
        if (key == "CancelProcessingHotkeys") ToolTipService.SetToolTip(copy,
            Loc.T("Saved global shortcut. Requests cancellation of final dictation processing or an active selected-text workflow without starting a recording. It does not cancel Recorder or file-transcription work. Use a main key; modifier-only shortcuts are unsupported."));
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
        // Buttons mark pointer presses handled, so listen to handled ones as well.
        AddHandler(PointerPressedEvent, new PointerEventHandler(CapturePointer), true);
        Unloaded += (_, _) => { StopTest(null); Cancel(false); };
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
    private string[] Current => ShortcutRules.Split(_values.GetValueOrDefault(_key, _defaultValue));
    private void Refresh()
    {
        _shortcuts.Children.Clear();
        var current = Current;
        if (current.Length == 0)
        {
            var empty = Button(Text(Loc.T("Set shortcut"), 12, true), "IconButtonStyle", Loc.T("Set {0} shortcut", _label));
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
                    Child = Text(ShortcutKeys.Label(keycap, ShortcutKeys.LayoutCharacter), 12)
                });
            var edit = Button(content, "IconButtonStyle", Loc.T("Edit {0} shortcut {1}", _label, chord));
            edit.MinHeight = 34; edit.Padding = new Thickness(2);
            edit.HorizontalAlignment = HorizontalAlignment.Right;
            edit.HorizontalContentAlignment = HorizontalAlignment.Left;
            edit.Click += (_, _) => Begin(index);
            ToolTipService.SetToolTip(edit, Loc.T("Change this shortcut"));
            row.Children.Add(edit);
            var remove = Button("×", "DestructiveButtonStyle", Loc.T("Remove {0} shortcut {1}", _label, chord));
            remove.MinWidth = 28; remove.MinHeight = 28; remove.Padding = new Thickness(4, 0, 4, 0);
            ToolTipService.SetToolTip(remove, Loc.T("Remove this shortcut"));
            remove.Click += (_, _) =>
            {
                SetValue(ShortcutRules.RemoveAt(Current, index));
                _add.Focus(FocusState.Keyboard);
            };
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            _shortcuts.Children.Add(row);
        }
        _record.Visibility = Visibility.Collapsed;
        StopTest(null);
        _repair.Visibility = Visibility.Collapsed;
        if (_test is not null) _test.Visibility = current.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = ""; _hint.Visibility = Visibility.Collapsed;
        _shell.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _record.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
    }
    internal void StartEditing() => Begin(Current.Length > 0 ? 0 : -1);

    private void Begin(int index = -1)
    {
        if (_commitRefreshQueued) return;
        StopTest(null);
        // A test in another editor would take the keys this editor is about to record.
        ShortcutTest.Stop();
        if (_active?.TryGetTarget(out var previous) == true && !ReferenceEquals(previous, this)) previous.Cancel(false);
        _active = new(this);
        _editingIndex = index;
        _startingCapture = true;
        IsEditing = IsCapturing = true; _candidate = _heldModifiers = ""; _hasMainKey = _altGr = false;
        _downModifiers.Clear();
        _value.Text = Loc.T("Press shortcut…");
        _record.Visibility = Visibility.Visible;
        _hint.Visibility = Visibility.Visible;
        _shell.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
        _shortcuts.IsHitTestVisible = false;
        foreach (var control in _shortcuts.Children.OfType<Grid>().SelectMany(row => row.Children).OfType<Control>().Concat(_shortcuts.Children.OfType<Control>())) control.IsEnabled = false;
        _hint.Text = Loc.T("Press keys or click here with the middle or a side mouse button, then choose Use shortcut. Esc cancels; Tab moves to the buttons.");
        _apply.IsEnabled = false;
        _normalActions.Visibility = Visibility.Collapsed; _editActions.Visibility = Visibility.Visible;
        _record.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
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
        if (_commitRefreshQueued) return;
        var conflict = ShortcutRules.Split(value).Select(chord => ShortcutRules.Conflict(chord, _key, _bindings())).FirstOrDefault(error => error is not null);
        if (conflict is not null) { _hint.Text = conflict; _hint.Visibility = Visibility.Visible; return; }
        var commitError = _commit?.Invoke(value);
        // Main may update _values to the actual native binding even on rollback failure.
        // Rebuild chips only after the current input callback has returned.
        if (commitError is null) _values[_key] = value;
        _commitRefreshQueued = true;
        _apply.IsEnabled = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            _commitRefreshQueued = false;
            if (!IsLoaded) return;
            if (IsEditing) Cancel(false); else Refresh();
            if (commitError is not null) { _hint.Text = commitError; _hint.Visibility = Visibility.Visible; }
        });
    }
    private void Apply()
    {
        if (_candidate.Length == 0 || Validate(_candidate) is not null) return;
        SetValue(ShortcutRules.Upsert(Current, _editingIndex, _candidate)); _add.Focus(FocusState.Keyboard);
    }
    // Cancellation uses RegisterHotKey rather than the dictation modifier-only hook.
    private string? Validate(string candidate) => ShortcutRules.Validate(candidate, allowModifiersOnly: _key is not ("CancelProcessingHotkeys" or "WorkflowSelectedTextHotkeys" or "RecentTranscriptionsHotkeys" or "CopyLastTranscriptionHotkeys" or "PasteLastTranscriptionHotkeys" or "ReadLastTranscriptionHotkeys" or "WorkflowPaletteHotkeys" or "RecorderToggleHotkeys"))
        ?? ShortcutRules.Duplicate(candidate, Current, _editingIndex)
        ?? ShortcutRules.AltGrConflict(candidate, ShortcutKeys.AltGrCharacter)
        ?? ShortcutRules.Conflict(candidate, _key, _bindings());
    private void Candidate(string candidate)
    {
        _candidate = candidate;
        var error = Validate(candidate);
        _value.Text = ShortcutKeys.Display(candidate, ShortcutKeys.LayoutCharacter);
        _hint.Text = error ?? Loc.T("Ready to use. Choose Use shortcut or press Enter to confirm.");
        _apply.IsEnabled = error is null;
    }
    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    // AltGr sets left Ctrl and right Alt. Like the dictation hook, count it only with a main key.
    private static string Modifiers(bool mainKey = true)
    {
        var altGr = !mainKey && DictationHotkeyRegistration.AltGrControlDown;
        return string.Join("+", new[]
        {
            Down(VirtualKey.RightControl) || Down(VirtualKey.Control) && !altGr ? "Ctrl" : "",
            Down(VirtualKey.LeftMenu) || Down(VirtualKey.Menu) && !altGr ? "Alt" : "", Down(VirtualKey.Shift) ? "Shift" : "",
            Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? "Win" : ""
        }.Where(value => value.Length > 0));
    }
    private static bool AltGrKey(KeyRoutedEventArgs e) => e.Key is VirtualKey.Control or VirtualKey.LeftControl && !e.KeyStatus.IsExtendedKey
        || e.Key is VirtualKey.Menu or VirtualKey.RightMenu && e.KeyStatus.IsExtendedKey;
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
        if (AltGrKey(e))
        {
            _altGr = DictationHotkeyRegistration.AltGrControlDown;
            if (_altGr) return;
        }
        if (Modifier(e.Key))
        {
            if (!_hasMainKey)
            {
                // The routed event can precede the thread's modifier-state update.
                // Include its key explicitly and retain the full chord through release.
                var pressed = ModifierName(e.Key);
                if (_downModifiers.Count == 0) { _heldModifiers = ""; _candidate = ""; _apply.IsEnabled = false; }
                _downModifiers.Add(pressed);
                _heldModifiers = string.Join("+", (_heldModifiers + "+" + Modifiers(mainKey: false) + "+" + pressed)
                    .Split('+', StringSplitOptions.RemoveEmptyEntries).Distinct());
                _value.Text = _heldModifiers.Replace("+", " + ") + " + …";
            }
            return;
        }
        _hasMainKey = true;
        var name = ShortcutKeys.Token((int)e.Key);
        Candidate(modifiers.Length == 0 ? name : $"{modifiers}+{name}");
    }
    private void ShowHint(string text)
    {
        if (_hint.Text != text) _hint.Text = text;
        _hint.Visibility = Visibility.Visible;
    }

    private string Show(string chord) => ShortcutKeys.Display(chord, ShortcutKeys.LayoutCharacter);

    private void StartTest()
    {
        _repair.Visibility = Visibility.Collapsed;
        if (ShortcutTest.Start(Current) is { } error) { ShowHint(error); return; }
        _testSession = ShortcutTest.Session;
        if (_test is not null) _test.Content = Text(Loc.T("Stop test"), 12);
        ShowHint(Loc.T("Press your shortcut now. Nothing starts during the test."));
        if (_testTimer is null)
        {
            _testTimer = DispatcherQueue.CreateTimer();
            _testTimer.Interval = TimeSpan.FromMilliseconds(100);
            _testTimer.Tick += (_, _) => TestTick();
        }
        _testTimer.Start();
    }

    private void StopTest(string? message)
    {
        _testTimer?.Stop();
        if (_testSession is null) return;
        // Another editor may have started its own test meanwhile; that one keeps running.
        if (ReferenceEquals(ShortcutTest.Session, _testSession)) ShortcutTest.Stop();
        _testSession = null;
        _testKeysHeld = message is not null;
        if (_test is not null) _test.Content = Text(Loc.T("Test"), 12);
        if (message is not null) ShowHint(message);
    }

    private void TestTick()
    {
        if (_testSession is not { } session) return;
        if (!IsLoaded) { StopTest(null); return; }
        if (!ReferenceEquals(ShortcutTest.Session, session)) { StopTest(Loc.T("Test stopped.")); return; }
        var verdict = session.Verdict(Environment.TickCount64, PausableKeys.Contains(_key) && ShortcutTest.Paused());
        if (verdict == ShortcutTestVerdict.Waiting)
        {
            if (session.Seen is { } received) ShowHint(Loc.T("Received {0}. Waiting for your shortcut…", Show(received)));
            return;
        }
        var seen = session.Seen is { } chord ? Show(chord) : "";
        // Repair restarts TypeWhisper's own hooks. A RegisterHotKey shortcut that Windows delivered but did not
        // pass on is taken by another app; restarting cannot change that.
        var hookShortcut = PausableKeys.Contains(_key) || session.DeliveredChord is { } delivered && ShortcutKeys.IsMouseChord(delivered);
        StopTest(verdict switch
        {
            ShortcutTestVerdict.Works => Loc.T("Works. TypeWhisper received {0}.", Show(session.Responded ?? "")),
            ShortcutTestVerdict.Paused => Loc.T("Dictation shortcuts are paused. Resume them from the tray icon."),
            ShortcutTestVerdict.NotResponding when hookShortcut =>
                Loc.T("Windows delivered your shortcut, but TypeWhisper didn't react. Choose Repair to restart its shortcut listener."),
            ShortcutTestVerdict.NotResponding =>
                Loc.T("Windows delivered your shortcut, but it didn't reach TypeWhisper. Another app may use the same keys; close keyboard tools one at a time and test again."),
            ShortcutTestVerdict.WrongOrder =>
                Loc.T("TypeWhisper received the keys of your shortcut in a different order. Hold the modifier keys first, then press the last key."),
            ShortcutTestVerdict.DifferentKeys when session.FunctionKeyArrivedAsMediaKey =>
                Loc.T("TypeWhisper received {0} instead of your shortcut. Your keyboard sends a media key there: turn on Fn Lock or hold Fn while you press it.", seen),
            ShortcutTestVerdict.DifferentKeys =>
                Loc.T("TypeWhisper received {0} instead of your shortcut. If you didn't press all of these keys, another app may be holding one down; press each key once and test again.", seen),
            _ when session.ExpectsFunctionKey =>
                Loc.T("TypeWhisper received nothing. Laptop function keys often change brightness or volume without sending a key: turn on Fn Lock or hold Fn."),
            _ => Loc.T("TypeWhisper received nothing. Another app may be intercepting the keyboard; close keyboard tools one at a time and test again.")
        });
        if (verdict == ShortcutTestVerdict.NotResponding && hookShortcut && ShortcutTest.Repair is not null) _repair.Visibility = Visibility.Visible;
    }

    private void Repair()
    {
        _repair.Visibility = Visibility.Collapsed;
        ShowHint(ShortcutTest.Repair?.Invoke() ?? Loc.T("Shortcut listener restarted. Choose Test to check again."));
    }

    // The middle and side mouse buttons, pressed over the editor. A shortcut that already uses the
    // button never arrives here; its mouse hook passes it on through CaptureRegisteredShortcut.
    private void CapturePointer(object sender, PointerRoutedEventArgs e)
    {
        if (!IsCapturing || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var key = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.MiddleButtonPressed => 0x04,
            PointerUpdateKind.XButton1Pressed => 0x05,
            PointerUpdateKind.XButton2Pressed => 0x06,
            _ => 0
        };
        if (key == 0) return;
        e.Handled = true;
        _hasMainKey = true;
        var modifiers = Modifiers();
        var name = ShortcutKeys.Token(key);
        Candidate(modifiers.Length == 0 ? name : $"{modifiers}+{name}");
    }
    internal void CaptureKeyUp(KeyRoutedEventArgs e)
    {
        if (!IsCapturing) return;
        // The hook has already seen AltGr's release here, so use the state from its key-down.
        // Windows releases AltGr's Ctrl before its right Alt.
        if (_altGr && AltGrKey(e))
        {
            _altGr = e.Key is VirtualKey.Control or VirtualKey.LeftControl;
            e.Handled = true; return;
        }
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
