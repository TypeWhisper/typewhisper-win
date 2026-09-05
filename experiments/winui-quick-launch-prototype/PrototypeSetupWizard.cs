using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;

namespace TypeWhisper.WinUIPrototype;

// Mac-inspired setup flow. Only the prototype's session dictionary is shared.
public sealed class PrototypeSetupWizard : UserControl
{
    private readonly Dictionary<string, string> _values;
    private readonly PrototypeSetupState _state;
    private readonly Action<bool> _exit;
    private readonly StackPanel _body = new() { Spacing = 18, MaxWidth = 670, Margin = new Thickness(24, 24, 24, 20) };
    private readonly Grid _progress = new() { ColumnSpacing = 6, MaxWidth = 740, Margin = new Thickness(24, 18, 24, 0) };
    private readonly TextBlock _message = Copy("", 12, true);
    private readonly HandCursorButton _next;
    private readonly HandCursorButton _back;
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _demo = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly List<PrototypeChoicePicker> _pickers = [];

    internal PrototypeSetupWizard(Dictionary<string, string> values, Action<bool> exit)
    {
        _values = values; _state = new(values); _exit = exit;
        var shell = new Grid { Background = Brush("InkBrush") };
        shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var i = 0; i < 9; i++) _progress.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(i % 2 == 0 ? 1 : 0.45, GridUnitType.Star)
        });
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var brand = Copy("TypeWhisper Setup", 16); brand.HorizontalAlignment = HorizontalAlignment.Center;
        header.Children.Add(brand); header.Children.Add(_progress); shell.Children.Add(header);
        _body.HorizontalAlignment = HorizontalAlignment.Center;
        _scroll = new ScrollViewer { Content = _body, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); shell.Children.Add(_scroll);
        var footer = new Grid { Padding = new Thickness(24, 16, 24, 16), ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _back = Button("← Back", () => { _state.Back(); Render(); }); footer.Children.Add(_back);
        var skip = Button("Skip setup", () => _exit(false)); Grid.SetColumn(skip, 2); footer.Children.Add(skip);
        _next = Button("Continue →", Next, true); Grid.SetColumn(_next, 3); footer.Children.Add(_next);
        var footerBorder = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brush("HairlineBrush") };
        Grid.SetRow(footerBorder, 2); shell.Children.Add(footerBorder);
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        Content = shell; Unloaded += (_, _) => _demo.Stop(); Render();
    }

    internal bool CloseOpenPicker()
    {
        var picker = _pickers.FirstOrDefault(p => p.IsPopupOpen);
        if (picker is null) return false;
        picker.ClosePopup(); return true;
    }
    private void Next()
    {
        if (_state.Validation is { } error) { _message.Text = error; return; }
        if (_state.Step == 4) { _state.Restart(); _exit(true); return; }
        _state.Next(); Render();
    }
    private void Render()
    {
        _demo.Stop(); _pickers.Clear(); _body.Children.Clear(); _progress.Children.Clear(); _message.Text = "";
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            var number = Copy(i < _state.Step ? "✓" : (i + 1).ToString(), 13);
            number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center;
            stack.Children.Add(new Border { Child = number, Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brush(i <= _state.Step ? "AccentBrush" : "ElevatedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(Copy(PrototypeSetupState.Steps[i], 12, i != _state.Step));
            var step = Button("", () => { _state.Revisit(index); Render(); });
            step.Content = stack; step.Style = (Style)Application.Current.Resources["PrototypeIconButtonStyle"];
            step.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(step, $"Step {i + 1} of 5: {PrototypeSetupState.Steps[i]}");
            AutomationProperties.SetItemStatus(step, i < _state.Step ? "Completed" : i == _state.Step ? "Current" : "Upcoming");
            if (i >= _state.Step) step.Content = null;
            FrameworkElement item = i < _state.Step ? step : new Border { Child = stack, Padding = new Thickness(8, 5, 8, 5) };
            if (i >= _state.Step) AutomationProperties.SetName(item, $"Step {i + 1} of 5: {PrototypeSetupState.Steps[i]}, {(i == _state.Step ? "current" : "upcoming")}");
            Grid.SetColumn(item, i * 2); _progress.Children.Add(item);
            if (i < 4)
            {
                var connector = new Border
                {
                    Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 20, 0, 0),
                    Background = Brush(i < _state.Step ? "AccentBrush" : "HairlineBrush"), IsHitTestVisible = false
                };
                AutomationProperties.SetAccessibilityView(connector, AccessibilityView.Raw);
                Grid.SetColumn(connector, i * 2 + 1); _progress.Children.Add(connector);
            }
        }
        _back.Visibility = _state.Step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = _state.Step == 4 ? "Finish preview" : "Continue →";
        string[] titles = ["Welcome to TypeWhisper", "Make room for your voice", "Choose your shortcut", "Choose your starting model", "Try your first dictation"];
        string[] subtitles = ["Set up voice typing in a few simple steps.", "Choose a microphone. Access will be checked in the finished app.",
            "Start and stop without leaving the app you are working in.", "Start locally. Extra AI and cloud providers can wait.", "See your words become text with a simulated recording."];
        var heading = Copy(titles[_state.Step], 28); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; heading.TextAlignment = TextAlignment.Center;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1); _body.Children.Add(heading);
        var subtitle = Copy(subtitles[_state.Step], 14, true); subtitle.TextAlignment = TextAlignment.Center; _body.Children.Add(subtitle);
        switch (_state.Step)
        {
            case 0:
                AddLogo();
                _body.Children.Add(Card("microphone", "Speak naturally", "Use a shortcut and dictate without breaking your flow.", false));
                _body.Children.Add(Card("text", "Keep your own words", "Insert text directly or review it first. You decide.", false));
                _body.Children.Add(Card("workflow", "Make it yours", "Add workflows and other engines when you need them.", false));
                break;
            case 1:
                _body.Children.Add(Card("microphone", "Microphone access · not checked", "The finished Windows app will check access before recording. This preview does not request permission or capture audio."));
                AddPicker("Microphone", "microphone", "SelectedMicrophoneDevice", "System default", ["System default", "Sample USB microphone", "Sample headset"]);
                _body.Children.Add(Card("text", "Text insertion · preview only", "The finished app will insert your transcript into the active text field. No text is sent to other apps during setup preview."));
                break;
            case 2:
                foreach (var (mode, detail) in new[] { ("Hybrid", "Tap to toggle. Hold to speak, release to stop."), ("Push to talk", "Hold to record, release to stop."), ("Toggle", "Press once to start and again to stop.") })
                {
                    var selected = _values.GetValueOrDefault("Mode", "Toggle") == mode;
                    var choice = Button("", () => { _values["Mode"] = mode; Render(); }, selected);
                    var content = new StackPanel { Spacing = 5 }; content.Children.Add(Copy($"{(selected ? "●" : "○")}  {mode}", 15)); content.Children.Add(Copy(detail, 12, true));
                    choice.Content = content; choice.Padding = new Thickness(16, 12, 16, 12); choice.HorizontalAlignment = HorizontalAlignment.Stretch; choice.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    AutomationProperties.SetName(choice, $"Recording mode {mode}"); AutomationProperties.SetItemStatus(choice, selected ? "Selected" : "Not selected");
                    _body.Children.Add(choice);
                }
                _body.Children.Add(new PrototypeShortcutRecorder("MainDictationHotkeys", "Main dictation", "Ctrl+Shift+F9", _values,
                    () => PrototypeSettingsCatalog.ShortcutBindings(_values)));
                _body.Children.Add(Copy("Add alternatives with +. Keys are recorded only inside this preview; no global shortcut is registered.", 12, true));
                break;
            case 3:
                var session = new PrototypeModelSession(_values);
                var picker = new PrototypeChoicePicker(); picker.Configure("Model", "chip", "Setup model");
                picker.SetOptions(PrototypeModelSession.Models.Where(m => session.IsDownloaded(m.Id))
                    .Select(m => new PrototypeChoice(m.Id, m.Title, m.Description)).ToArray(), session.Active);
                picker.SelectionChanged += id => { session.Activate(id); _message.Text = "Shared default model updated for this preview."; };
                _pickers.Add(picker); _body.Children.Add(picker);
                _body.Children.Add(Copy("Only downloaded samples are listed. Balanced is available in a fresh preview; more samples can be added under Models.", 12, true));
                AddPicker("Spoken language", "dictionary", "Language", "Automatic", ["Automatic", "English", "German", "French", "Spanish", "Italian"]);
                _body.Children.Add(Card("plugin", "Extra AI is optional", "Explore text providers and workflows later in Plugins. No account or API key is needed for this sample."));
                break;
            case 4:
                var model = PrototypeModelSession.Models.FirstOrDefault(m => m.Id == new PrototypeModelSession(_values).Active)?.Title ?? "Not selected";
                _body.Children.Add(Card("check", "Your preview is set up", $"{model} · {_values.GetValueOrDefault("Language", "Automatic")} · {_values.GetValueOrDefault("Mode", "Toggle")}"));
                AddPicker("After recording", "text", "AutoPaste", "On", ["On", "Off"], ["Insert directly", "Review first"]);
                AddTrial();
                break;
        }
        _body.Children.Add(_message);
        _body.Children.Add(Copy("SETUP PREVIEW · Session only. No permissions, downloads or audio capture.", 11, true));
        _scroll.ChangeView(null, 0, null, true);
        if (IsLoaded)
        {
            _next.Focus(FocusState.Keyboard);
            var visual = ElementCompositionPreview.GetElementVisual(_body);
            visual.StopAnimation("Opacity"); visual.Opacity = 1;
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(160);
                fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1);
                visual.StartAnimation("Opacity", fade);
            }
        }
    }
    private void AddTrial()
    {
        var transcript = Copy("Your sample transcript will appear here.", 16, true);
        _body.Children.Add(new Border { Child = transcript, Padding = new Thickness(20), MinHeight = 120, Background = Brush("SurfaceBrush"), CornerRadius = new CornerRadius(10) });
        var sample = _values.GetValueOrDefault("Language") == "German"
            ? "Das ist mein erstes Diktat mit TypeWhisper. Ich kann meinen Gedanken folgen, während meine Worte nach und nach als Text erscheinen."
            : "This is my first dictation with TypeWhisper. I can keep my train of thought and let the words appear at their own pace.";
        var words = sample.Split(' '); var count = 0;
        var tryButton = Button("Try sample dictation", () => { });
        EventHandler<object> tick = (_, _) =>
        {
            transcript.Text = string.Join(' ', words.Take(++count));
            if (count >= words.Length) { _demo.Stop(); tryButton.IsEnabled = true; _message.Text = "Sample complete. Nothing was recorded, copied or inserted into another app."; }
        };
        _demo.Tick += tick;
        tryButton.Click += (_, _) => { count = 0; transcript.Text = ""; transcript.Foreground = Brush("TextBrush"); tryButton.IsEnabled = false; _message.Text = "Playing a sample — microphone is off."; _demo.Start(); };
        tryButton.Unloaded += (_, _) => { _demo.Stop(); _demo.Tick -= tick; };
        _body.Children.Add(tryButton);
    }
    private void AddLogo()
    {
        _body.Children.Add(new PrototypeSetupLogo { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) });
    }
    private void AddPicker(string label, string icon, string key, string fallback, string[] options, string[]? labels = null)
    {
        var row = new StackPanel { Spacing = 8 }; row.Children.Add(Copy(label, 14));
        var picker = new PrototypeChoicePicker(); picker.Configure(label, icon, $"Setup {label}");
        picker.SetOptions(options.Select((value, i) => new PrototypeChoice(value, labels?[i] ?? value, "Session-only preview")).ToArray(), _values.GetValueOrDefault(key, fallback));
        picker.SelectionChanged += selected => _values[key] = selected;
        _pickers.Add(picker); row.Children.Add(picker); _body.Children.Add(row);
    }
    private static Border Card(string icon, string title, string description, bool filled = true)
    {
        var row = new Grid { ColumnSpacing = 16 }; row.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new());
        row.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 28, Height = 28 });
        var copy = new StackPanel { Spacing = 6 }; var heading = Copy(title, 16); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        copy.Children.Add(heading); copy.Children.Add(Copy(description, 13, true)); Grid.SetColumn(copy, 1); row.Children.Add(copy);
        return new Border { Child = row, Padding = new Thickness(20, filled ? 20 : 6, 20, filled ? 20 : 6), CornerRadius = new CornerRadius(12), Background = filled ? Brush("SurfaceBrush") : null };
    }
    private static HandCursorButton Button(string title, Action action, bool primary = false)
    {
        var button = new HandCursorButton { Content = title, MinHeight = 40, Style = (Style)Application.Current.Resources[primary ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"] };
        button.Click += (_, _) => action(); return button;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new()
    { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
}
