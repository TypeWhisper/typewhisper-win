using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

/// <summary>Guides explicit changes to the same persisted settings used by dictation.</summary>
public sealed class SetupWizard : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly Dictionary<string, string> _values;
    private readonly Action<bool> _exit;
    private readonly Func<string, string?> _commitHotkeys;
    private readonly Action<string> _openProvider;
    private readonly SetupState _state;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _message = Copy("");
    private readonly HandCursorButton _next;
    private readonly HandCursorButton _back;
    private readonly List<ChoicePicker> _pickers = [];
    private readonly SetupReadiness _feedback = new();
    private ChoicePicker? _providerPicker, _modelPicker, _languagePicker;
    private HandCursorButton? _configureProvider;
    private string? _selectedProvider;
    private string? _observedProvider;
    private bool _refreshingModels;
    private bool _closing;
    private bool _selecting;

    internal SetupWizard(Dictionary<string, string> values, Action<bool> exit,
        Func<string, string?> commitHotkeys, LocalDictationSession session, Action<string> openProvider)
    {
        _session = session; _values = values; _exit = exit; _commitHotkeys = commitHotkeys; _openProvider = openProvider;
        var store = new SetupPreferencesStore(WinUIProfile.DataPath("setup.json"));
        _state = new(store);
        var shell = new Grid { Padding = new Thickness(24), RowSpacing = 24, MaxWidth = 800 };
        shell.RowDefinitions.Add(new()); shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        shell.Children.Add(new ScrollViewer { Padding = (Thickness)Application.Current.Resources["VerticalScrollGutter"], Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _back = Button("Back", () => Move(Math.Max(0, _state.Step - 1))); footer.Children.Add(_back);
        var skip = Button("Skip setup", () => { if (!_closing) _exit(false); });
        Grid.SetColumn(skip, 2); footer.Children.Add(skip);
        _next = Button("Continue", Next);
        _next.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
        Grid.SetColumn(_next, 3); footer.Children.Add(_next);
        Grid.SetRow(footer, 1); shell.Children.Add(footer); Content = shell;
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Loaded += (_, _) => { _session.Changed += Changed; _session.Models.Changed += Changed; RefreshStatus(); };
        Unloaded += (_, _) => { _closing = true; _session.Changed -= Changed; _session.Models.Changed -= Changed; };
        _feedback.ReportPersistence(store.Error);
        Render();
    }
    internal bool CloseOpenPicker()
    {
        var picker = _pickers.FirstOrDefault(item => item.IsPopupOpen);
        if (picker is null) return false;
        picker.ClosePopup(); return true;
    }
    private void Changed() => DispatcherQueue.TryEnqueue(() => { if (!_closing && IsLoaded) { RefreshModelPickers(); RefreshStatus(); } });
    private string? Readiness()
    {
        bool microphoneAvailable;
        try { microphoneAvailable = _session.GetMicrophones().Count > 0; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Microphones could not be checked. Reopen the microphone step or skip setup."; }
        return SetupReadiness.Validate(_selecting || !_session.CanChangeProvider,
            !string.IsNullOrWhiteSpace(_session.Shortcut) && _session.Shortcut != "No shortcut assigned",
            _session.IsReady, microphoneAvailable);
    }

    private void RefreshStatus()
    {
        _next.Content = _state.Step == 4 ? "Finish setup" : "Continue";
        _back.IsEnabled = !_closing && !_selecting && _state.Step > 0;
        _next.IsEnabled = !_closing && !_selecting && (_state.Step != 4 || Readiness() is null);
        _message.Text = _feedback.Message(_state.Step == 4 ? Readiness() : "");
        _message.Visibility = string.IsNullOrWhiteSpace(_message.Text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Next()
    {
        if (_closing || _selecting) return;
        if (_state.Step == 4)
        {
            if (Readiness() is { } error) { _message.Text = error; return; }
            var saveError = _state.Complete(); _feedback.ReportPersistence(saveError);
            if (saveError is not null) { RefreshStatus(); return; }
            _exit(true); return;
        }
        Move(_state.Step + 1);
    }
    private void Move(int step)
    {
        if (_closing || _selecting) return;
        var error = _state.MoveTo(step); _feedback.ReportPersistence(error);
        if (error is not null) { RefreshStatus(); return; }
        Render();
    }
    private void Render()
    {
        _body.Children.Clear(); _pickers.Clear();
        _providerPicker = _modelPicker = _languagePicker = null; _configureProvider = null;
        var stepLabel = Copy($"SETUP · STEP {_state.Step + 1} OF 5", 11);
        stepLabel.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        _body.Children.Add(stepLabel);
        _body.Children.Add(Copy(SetupState.Steps[_state.Step], 24));
        _body.Children.Add(Copy("Your choices are saved immediately. You can return to setup at any time."));
        switch (_state.Step)
        {
            case 0:
                _body.Children.Add(Copy("Choose your microphone, shortcut, model and output preferences. Extra providers are optional. Cloud models send audio to their provider only when you explicitly start dictation."));
                break;
            case 1:
                var microphones = new MicrophonePriorityEditor(_session);
                _pickers.Add(microphones.AddPicker); _body.Children.Add(microphones);
                _body.Children.Add(Copy("TypeWhisper uses the first available microphone in this list, or your Windows default. You'll test it with your first dictation."));
                break;
            case 2:
                AddPicker("Recording mode", Enum.GetValues<RecordingMode>().Select(mode => new Choice(mode.ToString(), mode.ToString(), mode == RecordingMode.Hold ? "Hold the shortcut to record" : mode == RecordingMode.Hybrid ? "Tap to toggle, hold to speak" : "Press to start and stop")).ToArray(),
                    _session.RecordingModePreferences.Current.ToString(), id => _session.SelectRecordingMode(Enum.Parse<RecordingMode>(id)));
                _body.Children.Add(new ShortcutRecorder("MainDictationHotkeys", "Main dictation", "Ctrl+Shift+F9", _values,
                    () => SettingsCatalog.ShortcutBindings(_values), value => _closing ? "Setup is closed." : _commitHotkeys(value)));
                _body.Children.Add(Copy("The shortcut is active globally as soon as it is saved."));
                break;
            case 3:
                CreateModelPickers();
                break;
            case 4:
                _body.Children.Add(Copy($"Microphone: {_session.SelectedMicrophoneName}\nShortcut: {_session.Shortcut}\nMode: {_session.RecordingModePreferences.Current}\nModel: {_session.ActiveModelName}\nLanguage: {LanguageName(_session.Language)}"));
                AddPicker("After recording", [new("paste", "Insert directly", "Paste into the original target app"), new("review", "Review first", "Review and copy manually")],
                    _session.OutputPreferences.Current.AutoPaste ? "paste" : "review", id => _session.OutputPreferences.Save(_session.OutputPreferences.Current with { AutoPaste = id == "paste" }));
                AddPicker("Save history", [new("yes", "Save transcripts", "Save text to local history"), new("no", "Do not save", "Review remains available without history")],
                    _session.OutputPreferences.Current.SaveToHistory ? "yes" : "no", id => _session.OutputPreferences.Save(_session.OutputPreferences.Current with { SaveToHistory = id == "yes" }));
                _body.Children.Add(Copy("Open a text field in another app and use your shortcut for your first dictation."));
                break;
        }
        _body.Children.Add(_message); RefreshStatus();
    }
    private void CreateModelPickers()
    {
        _selectedProvider = _observedProvider = _session.ActiveProviderId;
        ChoicePicker Create(string label)
        {
            var picker = new ChoicePicker(); picker.Configure(label, "chip", label);
            _body.Children.Add(Copy(label, 12));
            _body.Children.Add(picker); _pickers.Add(picker); return picker;
        }
        _providerPicker = Create("Provider"); _modelPicker = Create("Ready model"); _languagePicker = Create("Spoken language");
        _providerPicker.SelectionChanged += id => { if (_refreshingModels || _closing) return; _selectedProvider = id; RefreshModelPickers(); };
        _modelPicker.SelectionChanged += async id =>
        {
            if (_refreshingModels || _closing || _selecting || _selectedProvider is null) return;
            _selecting = true; RefreshModelPickers(); RefreshStatus();
            try { _feedback.ReportPersistence(await _session.SelectProviderModelAsync(_selectedProvider, id)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _feedback.ReportPersistence("Model selection failed: " + ex.Message); }
            finally { _selecting = false; if (!_closing) { RefreshModelPickers(); RefreshStatus(); } }
        };
        _languagePicker.SelectionChanged += id =>
        {
            if (_refreshingModels || _closing) return;
            _feedback.ReportPersistence(_session.SelectLanguage(id)); RefreshModelPickers(); RefreshStatus();
        };
        _configureProvider = Button("Configure plugin", () =>
        {
            var provider = _session.DictationProviders.FirstOrDefault(item => item.Id == _selectedProvider);
            if (provider is not null) _openProvider(provider.PluginId);
        });
        _body.Children.Add(_configureProvider);
        _body.Children.Add(Copy("Choose a model to use its provider. Open plugin settings to download models or enter an API key."));
        RefreshModelPickers();
    }
    private static string LanguageName(string code)
    {
        if (code == "auto") return "Automatic";
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }
    private void RefreshModelPickers()
    {
        if (_closing || _providerPicker is null || _modelPicker is null || _languagePicker is null) return;
        _refreshingModels = true;
        try
        {
            if (_observedProvider != _session.ActiveProviderId) _selectedProvider = _observedProvider = _session.ActiveProviderId;
            var providers = _session.DictationProviders;
            var selected = providers.FirstOrDefault(item => item.Id == _selectedProvider);
            _providerPicker.SetOptions(providers.Select(item => new Choice(item.Id, item.Name, item.Status + (item.Cloud ? " - cloud" : " - on device"))).ToArray(), _selectedProvider ?? "", "Provider unavailable");
            _modelPicker.SetOptions(selected?.Models.Where(item => item.Ready).Select(item => new Choice(item.Id, item.Name, "Ready")).ToArray() ?? [],
                _selectedProvider == _session.ActiveProviderId ? selected?.SelectedModelId ?? "" : "", "Choose a ready model; otherwise configure the plugin");
            var codes = _session.SupportedLanguages;
            var options = (codes.Count == 0 || _session.UsesRegistryProvider ? new[] { "auto" }.Concat(codes) : codes).Distinct().ToArray();
            _languagePicker.SetOptions(options.Select(code => new Choice(code, LanguageName(code), "Supported by the active model")).ToArray(), _session.Language);
            var canChange = !_selecting && _session.CanChangeProvider;
            _providerPicker.IsEnabled = canChange;
            _modelPicker.IsEnabled = canChange && selected?.Ready == true;
            _languagePicker.IsEnabled = canChange && _session.IsReady && codes.Count > 0 && _selectedProvider == _session.ActiveProviderId;
            if (_configureProvider is not null) _configureProvider.IsEnabled = canChange && selected is not null;
        }
        finally { _refreshingModels = false; }
    }
    private void AddPicker(string label, IReadOnlyList<Choice> choices, string selected, Func<string, string?> save)
    {
        _body.Children.Add(Copy(label, 12));
        var picker = new ChoicePicker(); picker.Configure(label, "workflow", label); picker.SetOptions(choices, selected, "Saved selection unavailable");
        picker.SelectionChanged += id =>
        {
            if (_closing) return;
            var error = save(id);
            if (error is not null) picker.SetOptions(choices, selected);
            else selected = id;
            _feedback.ReportPersistence(error); RefreshStatus();
        };
        _pickers.Add(picker); _body.Children.Add(picker);
    }
    private HandCursorButton Button(string title, Action action)
    {
        var button = new HandCursorButton { Content = title, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        button.Click += (_, _) => { if (!_closing) action(); }; return button;
    }
    private static TextBlock Copy(string text, double size = 13) => new()
    { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextBrush"] };
}
