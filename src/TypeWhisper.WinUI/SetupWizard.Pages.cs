using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class SetupWizard
{
    private TextBox? _testBox;
    private string _testText = "";
    private bool _testSucceeded;
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private static Brush WizardBackground() => new LinearGradientBrush
    {
        StartPoint = new(0, 0), EndPoint = new(1, 0.7),
        GradientStops = { new() { Color = global::Windows.UI.Color.FromArgb(255, 7, 22, 29), Offset = 0 },
            new() { Color = global::Windows.UI.Color.FromArgb(255, 6, 10, 14), Offset = 1 } }
    };

    private void RenderSteps()
    {
        _steps.Children.Clear(); _steps.ColumnDefinitions.Clear();
        string[] labels = ["Welcome", "Permissions", "Hotkey", "AI & Engine", "Done"];
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0)
            {
                _steps.ColumnDefinitions.Add(new() { Width = new GridLength(0.55, GridUnitType.Star) });
                var line = new Border { Height = 1, Background = Resource(i <= _state.Step ? "AccentBrush" : "HairlineBrush"), Margin = new Thickness(6, 20, 6, 0), VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(line, i * 2 - 1); _steps.Children.Add(line);
            }
            _steps.ColumnDefinitions.Add(new());
            var stack = new StackPanel { Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center };
            var number = Copy(i < _state.Step ? "✓" : (i + 1).ToString(), 17); number.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center;
            stack.Children.Add(new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Resource(i <= _state.Step ? "AccentBrush" : "HairlineBrush"), Child = number });
            var label = Copy(labels[i], 13); label.TextAlignment = TextAlignment.Center; label.Foreground = Resource(i == _state.Step ? "TextBrush" : "MutedBrush");
            stack.Children.Add(label);
            AutomationProperties.SetName(stack, $"Step {i + 1}: {labels[i]}");
            AutomationProperties.SetItemStatus(stack, i == _state.Step ? "Current step" : i < _state.Step ? "Completed" : "Upcoming");
            Grid.SetColumn(stack, i * 2); _steps.Children.Add(stack);
        }
    }

    private Grid CardContent(string icon, string title, string description, string? badge = null)
    {
        var row = new Grid { ColumnSpacing = 18, MinHeight = 48 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center });
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var heading = Copy(title, 18); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; copy.Children.Add(heading);
        var detail = Copy(description, 15); detail.Foreground = Resource("MutedBrush"); copy.Children.Add(detail);
        Grid.SetColumn(copy, 1); row.Children.Add(copy);
        if (badge is not null)
        {
            var badgeText = Copy(badge, 13);
            if (badge is "Allowed" or "Available" or "Ready") badgeText.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 65, 211, 120));
            if (badge == "Selected") badgeText.Foreground = Resource("AccentBrush");
            var chip = new Border { Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(16), Background = Resource("HairlineBrush"), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
            Grid.SetColumn(chip, 2); row.Children.Add(chip);
        }
        return row;
    }

    private Border Card(UIElement child, bool selected = false) => new()
    {
        Child = child, Padding = new Thickness(18, 14, 18, 14), CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(1), BorderBrush = Resource(selected ? "AccentBrush" : "HairlineBrush"),
        Background = Resource(selected ? "ElevatedBrush" : "SurfaceBrush")
    };

    private void RenderWelcome()
    {
        _body.Children.Add(new SetupLogo { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 18) });
        var features = new StackPanel { Spacing = 24, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        features.Children.Add(CardContent("microphone", "Speak naturally", "Press a hotkey and speak in any app."));
        features.Children.Add(CardContent("text", "Write instantly", "Your words appear directly as text."));
        features.Children.Add(CardContent("sparkle", "Improve with AI", "Rewrite, translate, summarize and more."));
        _body.Children.Add(features);
    }

    private void RenderPermissions()
    {
        var microphone = new StackPanel { Spacing = 12 };
        microphone.Children.Add(CardContent("microphone", "Microphone access", "Required to record your voice.", MicrophoneAccessStatus()));
        microphone.Children.Add(Button("Open microphone settings", async () => await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"))));
        _body.Children.Add(Card(microphone));
        _body.Children.Add(Card(CardContent("keyboard", "Text insertion", "Windows allows typing into other apps. No separate accessibility permission is needed.", "Available")));
        var devices = new MicrophonePriorityEditor(_session); _pickers.Add(devices.AddPicker);
        var expander = new Expander { Header = "Choose microphone", Content = devices, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _body.Children.Add(expander);
        var note = Copy("You can change microphone access at any time in Windows settings."); note.Foreground = Resource("MutedBrush"); _body.Children.Add(note);
    }

    private static string MicrophoneAccessStatus()
    {
        try
        {
            return global::Windows.Devices.Enumeration.DeviceAccessInformation.CreateFromDeviceClass(global::Windows.Devices.Enumeration.DeviceClass.AudioCapture).CurrentStatus switch
            {
                global::Windows.Devices.Enumeration.DeviceAccessStatus.Allowed => "Allowed",
                global::Windows.Devices.Enumeration.DeviceAccessStatus.DeniedBySystem or global::Windows.Devices.Enumeration.DeviceAccessStatus.DeniedByUser => "Access blocked",
                _ => "Check in Windows"
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Check in Windows"; }
    }

    private void RenderHotkeys()
    {
        foreach (var (mode, title, description) in new[] {
            (RecordingMode.Hybrid, "Hybrid · Recommended", "Tap to toggle, hold for push-to-talk."),
            (RecordingMode.Hold, "Push-to-Talk", "Hold to record, release to stop."),
            (RecordingMode.Toggle, "Toggle", "Press to start, press again to stop.") })
        {
            var selected = _session.RecordingModePreferences.Current == mode;
            var button = Button(title, () => { _feedback.ReportPersistence(_session.SelectRecordingMode(mode)); Render(); });
            var content = CardContent("check", title, description, selected ? _session.Shortcut : null);
            content.Children.RemoveAt(0);
            content.Children.Add(new RadioButton { IsChecked = selected, IsHitTestVisible = false, IsTabStop = false, MinWidth = 22, Width = 22, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            StyleCardButton(button, selected);
            AutomationProperties.SetName(button, title); AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
            _body.Children.Add(button);
        }
        _shortcutRecorder = new ShortcutRecorder("MainDictationHotkeys", "Dictation hotkey", "Ctrl+Shift+F9", _values,
            () => SettingsCatalog.ShortcutBindings(_values), value => _closing ? "Setup is closed." : _commitHotkeys(value));
        _body.Children.Add(new Expander { Header = "Change hotkey", Content = _shortcutRecorder, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _body.Children.Add(Copy("Your existing hotkey is kept until you change it."));
    }

    private void RenderEngines()
    {
        _body.Children.Add(Card(CardContent("check", _session.IsReady ? _session.ActiveModelName + " is ready" : "Choose your dictation engine",
            _session.IsReady ? "Your selected model is ready for dictation." : "Download a local model or configure a provider to get started.")));
        foreach (var provider in _session.DictationProviders.Where(item => !item.Cloud || item.Id == _session.ActiveProviderId).OrderBy(item => item.Cloud))
        {
            var selected = provider.Id == _session.ActiveProviderId;
            var ready = provider.Models.FirstOrDefault(model => model.Ready);
            var button = Button(provider.Name, async () =>
            {
                if (_selecting || !_session.CanChangeProvider || (selected && _session.IsReady)) return;
                if (ready is null) { _openProvider(provider.PluginId); return; }
                _selecting = true; RefreshStatus();
                try { _feedback.ReportPersistence(await _session.SelectProviderModelAsync(provider.Id, ready.Id)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _feedback.ReportPersistence("Model selection failed: " + ex.Message); }
                finally { _selecting = false; if (!_closing) Render(); }
            });
            button.Content = CardContent(provider.Cloud ? "plugin" : "desktop", provider.Name + (!provider.Cloud ? " · On device" : " · Cloud"),
                provider.Cloud ? "Uses your provider account. Audio is sent to this service." : "Runs offline on your PC without an API key.",
                selected && _session.IsReady ? "Selected" : ready is not null ? "Ready" : "Configure");
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            StyleCardButton(button, selected);
            AutomationProperties.SetName(button, provider.Name);
            _body.Children.Add(button);
        }
        var start = _body.Children.Count;
        CreateModelPickers();
        var configuration = new StackPanel { Spacing = 10 };
        while (_body.Children.Count > start) { var child = _body.Children[start]; _body.Children.RemoveAt(start); configuration.Children.Add(child); }
        _body.Children.Add(new Expander { Header = "Model and language", Content = configuration, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _body.Children.Add(Copy("Cloud providers and optional AI text processing can be configured later in Integrations."));
    }

    private void RenderTest()
    {
        var shortcut = CardContent("keyboard", _session.Shortcut, ""); shortcut.HorizontalAlignment = HorizontalAlignment.Center; _body.Children.Add(shortcut);
        _testBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180, FontSize = 18, Padding = new Thickness(18), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Text = _testText, PlaceholderText = "Your dictation appears here…" };
        AutomationProperties.SetName(_testBox, "Try dictation");
        _testBox.TextChanged += (_, _) => { if (_testBox is not null) _testText = _testBox.Text; };
        _body.Children.Add(_testBox);
        _body.Children.Add(Card(CardContent("sparkle", "Try it out", "Click the text field, press your hotkey and say something. Test text stays in this setup window.")));
    }

    private Action<string>? CaptureTestTarget(IntPtr window)
    {
        if (_closing || _state.Step != 4 || _testBox is null || FocusManager.GetFocusedElement(XamlRoot) != _testBox) return null;
        if (Microsoft.UI.Win32Interop.GetWindowIdFromWindow(window).Value != XamlRoot.ContentIslandEnvironment.AppWindowId.Value) return null;
        var target = _testBox;
        return text => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || _testBox != target || !target.IsLoaded) return;
            target.SelectedText = text; _testText = target.Text; _testSucceeded = true;
            _message.Text = "Your first dictation worked."; _message.Visibility = Visibility.Visible;
        });
    }
    private static void StyleCardButton(HandCursorButton button, bool selected)
    {
        button.Padding = new Thickness(18, 14, 18, 14); button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(12);
        button.Background = Resource(selected ? "ElevatedBrush" : "SurfaceBrush");
        button.BorderBrush = Resource(selected ? "AccentBrush" : "HairlineBrush");
    }
}
