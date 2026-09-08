using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

/// <summary>Guides explicit changes to the same persisted settings used by dictation.</summary>
public sealed partial class SetupWizard : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly Dictionary<string, string> _values;
    private readonly Action<bool> _exit;
    private readonly Func<string, string?> _commitHotkeys;
    private readonly Action<string> _openProvider;
    private readonly SetupState _state;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly Grid _steps = new() { Margin = new Thickness(0, 24, 0, 0) };
    private readonly ScrollViewer _scroll;
    private ShortcutRecorder? _shortcutRecorder;
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
        var shell = new Grid { Padding = new Thickness(32, 18, 32, 24), RowSpacing = 28, Background = WizardBackground() };
        shell.RowDefinitions.Add(new() { Height = GridLength.Auto }); shell.RowDefinitions.Add(new()); shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Stretch };
        var title = Copy("TypeWhisper Setup", 20); title.HorizontalAlignment = HorizontalAlignment.Center; title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        heading.Children.Add(title); heading.Children.Add(_steps); shell.Children.Add(heading);
        _body.MaxWidth = 720; _body.HorizontalAlignment = HorizontalAlignment.Center;
        _scroll = new ScrollViewer { Padding = new Thickness(12, 0, 12, 12), Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Center };
        _scroll.SizeChanged += (_, _) => _body.Width = Math.Max(0, Math.Min(720, _scroll.ActualWidth - 24));
        Grid.SetRow(_scroll, 1); shell.Children.Add(_scroll);
        var footer = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 18, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Resource("HairlineBrush") };
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
        Grid.SetRow(footer, 2); shell.Children.Add(footer); Content = shell;
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        PageKeyboardNavigation.Attach(shell);
        PreviewKeyDown += (_, e) =>
        {
            if (_shortcutRecorder is { IsEditing: true } recorder && (recorder.IsCapturing || e.Key == global::Windows.System.VirtualKey.Escape))
                recorder.CaptureKeyDown(e);
        };
        KeyUp += (_, e) => { if (_shortcutRecorder?.IsCapturing == true) _shortcutRecorder.CaptureKeyUp(e); };
        Loaded += (_, _) => { _closing = false; _session.Changed += Changed; _session.Models.Changed += Changed; _session.SetupTestTarget = CaptureTestTarget; RefreshStatus(); _testBox?.Focus(FocusState.Programmatic); };
        Unloaded += (_, _) => { _closing = true; _session.Changed -= Changed; _session.Models.Changed -= Changed; _session.SetupTestTarget = null; };
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
    private string? Readiness(bool currentStep = false)
    {
        bool microphoneAvailable;
        try { microphoneAvailable = _session.GetMicrophones().Count > 0; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Microphones could not be checked. Reopen the microphone step or skip setup."; }
        if ((currentStep ? _state.Step : 4) > 0 && MicrophoneAccessStatus() == "Access blocked")
            return "Allow microphone access in Windows settings to continue, or skip setup.";
        return SetupReadiness.ValidateStep(currentStep ? _state.Step : 4, _selecting || !_session.CanChangeProvider,
            !string.IsNullOrWhiteSpace(_session.Shortcut) && _session.Shortcut != "No shortcut assigned",
            _session.IsReady, microphoneAvailable);
    }

    private void RefreshStatus()
    {
        _next.Content = _state.Step == 4 ? "Finish setup" : "Continue";
        _back.Visibility = _state.Step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _back.IsEnabled = !_closing && !_selecting && _state.Step > 0;
        _next.IsEnabled = !_closing && !_selecting && Readiness(currentStep: true) is null;
        _message.Text = _feedback.Message(Readiness(currentStep: true) ?? (_state.Step == 4 && _testSucceeded ? "Your first dictation worked." : ""));
        _message.Visibility = string.IsNullOrWhiteSpace(_message.Text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Next()
    {
        if (_closing || _selecting) return;
        if (Readiness(currentStep: true) is not null) { RefreshStatus(); return; }
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
        _body.Children.Clear(); _pickers.Clear(); _shortcutRecorder = null; _testBox = null;
        _providerPicker = _modelPicker = _languagePicker = null; _configureProvider = null;
        RenderSteps();
        string[] titles = ["Welcome to TypeWhisper", "Permissions", "Choose your hotkey", "AI & Engine", "Try it out"];
        string[] subtitles = ["Set up voice typing in a few simple steps.", "Give TypeWhisper access to work on your PC.",
            "Start and stop dictation without leaving your app.", "Local defaults first. Cloud providers can wait.", "Press your hotkey and say something."];
        var title = Copy(titles[_state.Step], 27); title.FontWeight = Microsoft.UI.Text.FontWeights.Bold; title.TextAlignment = TextAlignment.Center;
        _body.Children.Add(title);
        var subtitle = Copy(subtitles[_state.Step], 17); subtitle.Foreground = Resource("MutedBrush"); subtitle.TextAlignment = TextAlignment.Center; subtitle.Margin = new Thickness(0, 0, 0, 12);
        _body.Children.Add(subtitle);
        switch (_state.Step)
        {
            case 0: RenderWelcome(); break;
            case 1: RenderPermissions(); break;
            case 2: RenderHotkeys(); break;
            case 3: RenderEngines(); break;
            case 4: RenderTest(); break;
        }
        _body.Children.Add(_message); RefreshStatus();
        _scroll.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || !IsLoaded) return;
            if (_testBox is not null) _testBox.Focus(FocusState.Programmatic);
            else (_next.IsEnabled ? _next : _back).Focus(FocusState.Programmatic);
        });
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
