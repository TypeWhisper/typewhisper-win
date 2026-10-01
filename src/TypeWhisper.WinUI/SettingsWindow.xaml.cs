using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.WinUI.Platform;
using global::Windows.Graphics;
using global::Windows.UI;

namespace TypeWhisper.WinUI;

public sealed partial class SettingsWindow : Window
{
    private OverlayPreferences _preferences;
    internal Func<Action<bool>, SetupWizard>? CreateSetupWizard { get; set; }
    internal Func<string, string?>? CommitDictationHotkeys { get; set; }
    internal Func<string, string?>? CommitCancelProcessingHotkeys { get; set; }
    internal Func<string, string?>? CommitRecentTranscriptionsHotkeys { get; set; }
    internal Func<string, string?>? CommitCopyLastTranscriptionHotkeys { get; set; }
    internal Func<string, string?>? CommitPasteLastTranscriptionHotkeys { get; set; }
    internal Func<string, string?>? CommitReadLastTranscriptionHotkeys { get; set; }
    internal Func<string, string?>? CommitWorkflowPaletteHotkeys { get; set; }
    internal Func<string, string, string?>? CommitRecordingShortcut { get; set; }
    internal Func<string, string?>? CommitRecorderHotkeys { get; set; }
    internal Action<string, StackPanel, List<ChoicePicker>>? ConfigureLiveSettings { get; set; }
    // Pages that host an app view instead of catalog settings. The same view instance
    // moves into whichever settings window is open.
    internal static readonly string[] WorkspaceCategories = ["Home", "File transcription", "Recorder", "Statistics", "Dictionary", "Snippets", "Workflows", "Sync & backup"];
    internal Func<string, FrameworkElement?>? WorkspacePage
    {
        get => _workspacePage;
        set
        {
            _workspacePage = value;
            // The window opens on Home before its owner connects the pages.
            if (WorkspaceCategories.Contains(_currentCategory) && SetupHost.Child is null) ShowCategoryCore(_currentCategory);
        }
    }
    private Func<string, FrameworkElement?>? _workspacePage;
    internal Func<string, bool>? WorkspaceBack { get; set; }
    internal Action<string, KeyRoutedEventArgs>? WorkspaceKey { get; set; }
    // The workspace category now shown, or null when none is.
    internal event Action<string?>? WorkspaceChanged;
    internal static string DisplayName(string category) => category switch
    {
        "Privacy" => Loc.T("History & Sync"),
        "Files & recovery" => Loc.T("Recovery"),
        _ => Loc.T(category)
    };
    internal void SetIntegrationsContent(UIElement content) => IntegrationsHost.Child = content;
    internal void DetachIntegrationsContent() => IntegrationsHost.Child = null;
    internal Func<bool>? NavigateIntegrationBack { get; set; }
    internal Func<Task<bool>>? CanLeaveIntegrationAsync { get; set; }
    private bool _checkingNavigation;
    private bool _allowClose;
    private readonly StackPanel _integrationNavigation = new() { Spacing = 2 };
    private readonly List<HandCursorButton> _pluginNavigationButtons = [];
    private (string Id, string Title)[] _integrationItems = [];
    internal event Action<string?>? IntegrationRequested;
    internal event Action? IntegrationDismissed;
    private bool _updating = true;
    private bool _liveTranscriptionAvailable = true;
    internal void SetLiveTranscriptionAvailability(bool available)
    {
        if (_liveTranscriptionAvailable == available) return;
        _liveTranscriptionAvailable = available;
        SetPreferences(_preferences);
    }
    private uint _dpi;
    private bool _positioning;
    private bool _changingSearch;
    private bool _searchActive;
    private string _currentCategory = "Home";
    private readonly List<HandCursorButton> _searchButtons = [];
    internal event Action<OverlayPreferences>? PreferencesChanged;
    internal event EventHandler? PreviewRequested;
    internal event EventHandler? PausePreviewRequested;
    internal event Action? PreviewDismissed;
    internal event Action<double, double>? PreviewSizeRequested;
    private bool _previewVisible;
    private bool _updatingPreviewSize;
    private LiveTextPreviewFrame? _floatingPreviewFrame;

    private readonly Dictionary<string, string> _values;
    private readonly List<ChoicePicker> _catalogPickers = [];
    private readonly List<ChoicePicker> _appearancePickers = [];
    private readonly List<HandCursorButton> _navigationButtons = [];
    internal SettingsWindow(OverlayPreferences preferences, Dictionary<string, string> values)
    {
        _values = values;
        _preferences = preferences;
        InitializeComponent();
        LocalizeXamlText();
        PageKeyboardNavigation.Attach(SettingsRoot);
        NativeWindowAppearance.ApplyAppTitleBar(this);
        CatalogContent.LayoutUpdated += (_, _) => SettingsCatalog.UpdateTrailingSeparators(CatalogContent);
        AppToggleSwitch.Configure(LiveTextToggle);
        AppToggleSwitch.Configure(DetailsToggle);
        OverlayEditor.Changed += Publish;
        // Same order as the macOS settings sidebar. Groups are separated without headings;
        // an empty group holds the plugin pages.
        (string Category, string Icon)[][] groups =
        [
            [(Loc.Mark("Home"), "home")],
            [(Loc.Mark("General"), "settings"), (Loc.Mark("Appearance"), "desktop"), (Loc.Mark("Dictation"), "microphone"), (Loc.Mark("Audio"), "speaker"), (Loc.Mark("Files & recovery"), "restore"),
             (Loc.Mark("Shortcuts"), "keyboard"), (Loc.Mark("File transcription"), "file"), (Loc.Mark("Recorder"), "recorder")],
            [(Loc.Mark("Privacy"), "history"), (Loc.Mark("Statistics"), "stats"), (Loc.Mark("Dictionary"), "dictionary"), (Loc.Mark("Snippets"), "text"), (Loc.Mark("Workflows"), "workflow"), (Loc.Mark("Premium"), "lock")],
            [],
            [(Loc.Mark("Sync & backup"), "devices"), (Loc.Mark("Advanced"), "settings"), (Loc.Mark("Account & about"), "info")]
        ];
        foreach (var group in groups)
        {
            var section = new StackPanel { Spacing = 2 };
            if (group.Length == 0) section.Children.Add(_integrationNavigation);
            foreach (var (category, icon) in group)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 18, Height = 18 });
                row.Children.Add(new TextBlock { Text = DisplayName(category), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
                var button = new HandCursorButton { Content = row, Tag = category, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 36, Padding = new Thickness(10, 7, 10, 7),
                    Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
                AutomationProperties.SetName(button, Loc.T("Settings category {0}", DisplayName(category)));
                button.Click += (_, _) => ShowCategory(category);
                section.Children.Add(button);
                _navigationButtons.Add(button);
            }
            SettingsNavigation.Children.Add(section);
        }
        SettingsCatalog.RenderLiveTextOptions(LiveTextOptions, _values, _appearancePickers);
        foreach (var picker in _appearancePickers)
        {
            if (picker.Tag is "LiveTranscriptionFontSize") picker.SelectionChanged += value =>
            {
                if (!_updating && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var size))
                    Publish(_preferences with { LiveTranscriptionFontSize = size });
            };
            else if (picker.Tag is "LiveTextPlacement") picker.SelectionChanged += value =>
            {
                if (!_updating) Publish(_preferences with { FloatingLiveText = value == "Floating window" });
            };
            else if (picker.Tag is "PreviewBubbleAutoHideMilliseconds") picker.SelectionChanged += value =>
            {
                if (!_updating) Publish(_preferences with { PreviewBubbleAutoHideMilliseconds = DurationChoices.First(c => c.Label == value).Milliseconds });
            };
        }
        SessionHint.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateFooter());
        EditorPreviewButton.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateFooter());
        ShowCategory("Home");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsDragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsMaximizable = false;
        }
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        NativeWindowAppearance.RemoveSystemBorder(this);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                NativeWindowAppearance.RemoveSystemBorder(this);
            else foreach (var recorder in Descendants(SettingsRoot).OfType<ShortcutRecorder>()) recorder.Cancel(false);
        };
        AppWindow.Changed += (_, args) =>
        {
            if (_positioning || !args.DidPositionChange) return;
            var currentDpi = NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (currentDpi != _dpi) PlaceOn(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
        };
        SetPreferences(preferences);
        Closed += (_, _) =>
        {
            _ = ShutdownSetupImportAsync();
            // Release the hosted view so the next settings window can show it.
            WorkspaceHost.Child = null;
            WorkspaceChanged?.Invoke(null);
        };
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            await RequestCloseAsync();
        };
    }

    // The XAML keeps the English text as a fallback.
    private void LocalizeXamlText()
    {
        Title = Loc.T("TypeWhisper Settings");
        SettingsBrandTitle.Text = Loc.T("Settings");
        AutomationProperties.SetName(CloseSettingsButton, Loc.T("Close settings"));
        AutomationProperties.SetName(SettingsSearch, Loc.T("Search settings"));
        SearchPlaceholder.Text = Loc.T("Search settings…");
        AutomationProperties.SetName(ClearSettingsSearch, Loc.T("Clear settings search"));
        AppearanceTitle.Text = Loc.T("Appearance");
        AppearanceSubtitle.Text = Loc.T("Choose how much you want to see while you speak.");
        RecordingOverlayHeading.Text = Loc.T("RECORDING OVERLAY");
        AutomationProperties.SetName(StandardChoice, Loc.T("Settings standard overlay"));
        StandardChoiceTitle.Text = Loc.T("Standard");
        StandardChoiceDescription.Text = Loc.T("Waveform, status and time");
        AutomationProperties.SetName(CompactChoice, Loc.T("Settings compact overlay"));
        CompactChoiceTitle.Text = Loc.T("Compact");
        CompactChoiceDescription.Text = Loc.T("A slim waveform with time");
        AutomationProperties.SetName(MinimalChoice, Loc.T("Settings minimal overlay"));
        MinimalChoiceTitle.Text = Loc.T("Minimal");
        MinimalChoiceDescription.Text = Loc.T("Just an indicator at the edge");
        LiveTextTitle.Text = Loc.T("Live transcription");
        AutomationProperties.SetName(LiveTextToggle, Loc.T("Settings live transcription"));
        DetailsTitle.Text = Loc.T("Technical details");
        AutomationProperties.SetName(DetailsToggle, Loc.T("Settings technical details"));
        CustomizeLayoutButton.Content = Loc.T("Customize layout");
        AutomationProperties.SetName(CustomizeLayoutButton, Loc.T("Customize overlay layout"));
        PreviewButton.Content = Loc.T("Preview overlay");
        AutomationProperties.SetName(PreviewButton, Loc.T("Preview overlay"));
        PausePreviewButton.Content = Loc.T("Pause preview");
        AutomationProperties.SetName(PausePreviewButton, Loc.T("Pause or resume preview"));
        PreviewExplanation.Text = Loc.T("Preview uses your microphone level when available, otherwise a simulated signal. Transcript text is a sample; no audio is saved.");
        BackToAppearanceButton.Content = Loc.T("← Appearance");
        AutomationProperties.SetName(BackToAppearanceButton, Loc.T("Back to appearance"));
        FloatingLiveTextHeading.Text = Loc.T("FLOATING LIVE TEXT");
        PreviewWidth.Header = Loc.T("Width");
        AutomationProperties.SetName(PreviewWidth, Loc.T("Floating live-text width"));
        PreviewHeight.Header = Loc.T("Height");
        AutomationProperties.SetName(PreviewHeight, Loc.T("Floating live-text height"));
        EditorPreviewButton.Content = Loc.T("Preview overlay");
        AutomationProperties.SetName(EditorPreviewButton, Loc.T("Layout editor overlay preview"));
    }

    internal void ShowOn(DisplayArea area)
    {
        PlaceOn(area);
        AppWindow.Show();
        Activate();
    }

    private void PlaceOn(DisplayArea area)
    {
        _positioning = true;
        try
        {
            var work = area.WorkArea;
            AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
            _dpi = NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var scale = (_dpi == 0 ? 96 : _dpi) / 96d;
            // Reproducible logical-viewport test, deliberately not an OS DPI override.
            var smallPreview = Environment.GetCommandLineArgs().Contains("--settings-small");
            var width = Math.Min((int)Math.Round((smallPreview ? 740 : 1040) * scale), Math.Max(1, work.Width - (int)(48 * scale)));
            var height = Math.Min((int)Math.Round((smallPreview ? 560 : 780) * scale), Math.Max(1, work.Height - (int)(48 * scale)));
            AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2, width, height));
            NativeWindowAppearance.RemoveSystemBorder(this);
        }
        finally { _positioning = false; }
    }

    internal void SetPreferences(OverlayPreferences preferences)
    {
        _updating = true;
        _preferences = preferences;
        var size = preferences.LiveTranscriptionFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _values["LiveTranscriptionFontSize"] = size;
        var placement = preferences.FloatingLiveText ? "Floating window" : "Attached to recording";
        _values["LiveTextPlacement"] = placement;
        _appearancePickers.FirstOrDefault(p => p.Tag is "LiveTextPlacement")?.SetOptions(
            new[] { "Attached to recording", "Floating window" }.Select(label => new Choice(label, Loc.T(label), Loc.T("Saved on this device"))).ToArray(), placement, Loc.T(placement));
        var duration = DurationChoices.FirstOrDefault(c => c.Milliseconds == preferences.PreviewBubbleAutoHideMilliseconds).Label
            ?? $"{preferences.PreviewBubbleAutoHideMilliseconds} milliseconds";
        _values["PreviewBubbleAutoHideMilliseconds"] = duration;
        _appearancePickers.FirstOrDefault(p => p.Tag is "LiveTranscriptionFontSize")?.SetOptions(
            Enumerable.Range(10, 9).Select(n => new Choice(n.ToString(), n.ToString(), Loc.T("Saved on this device"))).ToArray(), size, size);
        _appearancePickers.FirstOrDefault(p => p.Tag is "PreviewBubbleAutoHideMilliseconds")?.SetOptions(
            DurationChoices.Select(c => new Choice(c.Label, Loc.T(c.Label), Loc.T("After successful paste; errors remain visible for five seconds"))).ToArray(), duration, Loc.T("{0} milliseconds", preferences.PreviewBubbleAutoHideMilliseconds));
        OverlayEditor.SetPreferences(preferences);
        foreach (var button in new[] { StandardChoice, CompactChoice, MinimalChoice })
        {
            var selected = (string)button.Tag == preferences.Mode.ToString();
            button.Style = (Style)Application.Current.Resources[selected ? "PrimaryButtonStyle" : "SecondaryButtonStyle"];
            AutomationProperties.SetItemStatus(button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
        }
        LiveTextToggle.IsOn = _liveTranscriptionAvailable && preferences.LiveText;
        DetailsToggle.IsOn = preferences.TechnicalDetails;
        var minimal = preferences.Mode == OverlayMode.Minimal;
        var standard = preferences.Mode == OverlayMode.Standard;
        LiveTextToggle.IsEnabled = !minimal && _liveTranscriptionAvailable;
        DetailsToggle.IsEnabled = standard;
        LiveTextDescription.Text = !_liveTranscriptionAvailable
            ? Loc.T("Unavailable for the selected provider or task. Text arrives after recording stops. Your live-text preference is kept for supported models.")
            : minimal
            ? Loc.T("Hidden in Minimal. Your preference is kept for Standard and Compact.")
            : preferences.FloatingLiveText
            ? Loc.T("Show live text in a floating window. Drag its header to move it or its edges to resize it. Longer text scrolls.")
            : Loc.T("Show streaming text beside the recording block. Longer text scrolls.");
        DetailsDescription.Text = standard
            ? Loc.T("Show the audio level in dBFS and measured render frequency. Off by default.")
            : Loc.T("Available in Standard only. Your preference is kept when switching layouts.");
        UpdatePreviewSizeControls();
        _updating = false;
    }

    internal void SetPreviewVisible(bool visible, bool paused = false)
    {
        if (visible && !_previewVisible) RefreshPreviewSize();
        _previewVisible = visible;
        UpdatePreviewSizeControls();
        PreviewButton.Content = visible ? Loc.T("Stop preview") : Loc.T("Preview overlay");
        EditorPreviewButton.Content = visible ? Loc.T("Stop preview") : Loc.T("Preview overlay");
        PausePreviewButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PausePreviewButton.Content = paused ? Loc.T("Resume preview") : Loc.T("Pause preview");
    }

    private void UpdatePreviewSizeControls()
    {
        PreviewSizeSection.Visibility = _preferences.FloatingLiveText ? Visibility.Visible : Visibility.Collapsed;
        PreviewWidth.IsEnabled = PreviewHeight.IsEnabled = _previewVisible && _preferences.LiveText && _preferences.Mode != OverlayMode.Minimal;
        FloatingPositionLabel.Text = _previewVisible ? Loc.T("POSITION ON SCREEN") : Loc.T("LAST PREVIEW POSITION");
        PreviewSizeHint.Text = PreviewWidth.IsEnabled
            ? Loc.T("Changes are saved automatically. Size uses display-independent pixels and is limited to your screen's work area.")
            : Loc.T("Start the preview with live text enabled in Standard or Compact to adjust its width and height.");
    }

    internal void SetFloatingPlacement(LiveTextPreviewFrame? frame)
    {
        _floatingPreviewFrame = frame;
        if (frame is not null) SetPreviewSizeValues(frame.Width, frame.Height);
        FloatingPositionPreview.Visibility = frame is null ? Visibility.Collapsed : Visibility.Visible;
        RenderFloatingPosition();
    }

    private void FloatingScreen_SizeChanged(object sender, SizeChangedEventArgs e) => RenderFloatingPosition();

    private void RenderFloatingPosition()
    {
        if (_floatingPreviewFrame is not { } frame) return;
        var map = LiveTextPlacement.Project(frame, FloatingScreenCanvas.ActualWidth, FloatingScreenCanvas.Height);
        Place(FloatingScreenBounds, map.Screen);
        Place(FloatingWindowThumb, map.Window);
        var horizontal = 100d * (frame.Window.X - frame.WorkArea.X) / Math.Max(1, frame.WorkArea.Width);
        var vertical = 100d * (frame.Window.Y - frame.WorkArea.Y) / Math.Max(1, frame.WorkArea.Height);
        FloatingPositionSummary.Text = Loc.T("Live text · {0:0}% from left · {1:0}% from top", horizontal, vertical);
        AutomationProperties.SetName(FloatingPositionPreview, FloatingPositionSummary.Text);

        static void Place(FrameworkElement element, LiveTextMapRect rect)
        {
            Canvas.SetLeft(element, rect.X);
            Canvas.SetTop(element, rect.Y);
            element.Width = rect.Width;
            element.Height = rect.Height;
        }
    }

    private void RefreshPreviewSize()
    {
        var saved = LiveTextPlacement.Read(WinUIProfile.DataPath("live-text-position.json"));
        SetPreviewSizeValues(_floatingPreviewFrame?.Width ?? saved?.Width ?? 420,
            _floatingPreviewFrame?.Height ?? saved?.Height ?? 220);
    }

    private void SetPreviewSizeValues(double width, double height)
    {
        var wasUpdating = _updatingPreviewSize;
        _updatingPreviewSize = true;
        try
        {
            PreviewWidth.Value = Math.Round(width);
            PreviewHeight.Value = Math.Round(height);
        }
        finally { _updatingPreviewSize = wasUpdating; }
    }

    private void PreviewSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating || _updatingPreviewSize || !PreviewWidth.IsEnabled || !double.IsFinite(args.NewValue)) return;
        // Preserve the other dimension if the window was resized with the pointer since this page opened.
        var saved = LiveTextPlacement.Read(WinUIProfile.DataPath("live-text-position.json"));
        var width = ReferenceEquals(sender, PreviewWidth) ? args.NewValue : _floatingPreviewFrame?.Width ?? saved?.Width ?? PreviewWidth.Value;
        var height = ReferenceEquals(sender, PreviewHeight) ? args.NewValue : _floatingPreviewFrame?.Height ?? saved?.Height ?? PreviewHeight.Value;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        PreviewSizeRequested?.Invoke(width, height);
        RefreshPreviewSize();
    }

    private void PausePreview_Click(object sender, RoutedEventArgs e) => PausePreviewRequested?.Invoke(this, EventArgs.Empty);

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode } || !Enum.TryParse<OverlayMode>(mode, out var selected)) return;
        Publish(_preferences with { Mode = selected });
    }

    private void Preference_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Publish(_preferences with { LiveText = _liveTranscriptionAvailable ? LiveTextToggle.IsOn : _preferences.LiveText, TechnicalDetails = DetailsToggle.IsOn });
    }

    private void Publish(OverlayPreferences preferences)
    {
        SetPreferences(preferences);
        PreferencesChanged?.Invoke(preferences);
    }

    private static readonly (string Label, int Milliseconds)[] DurationChoices =
    [(Loc.Mark("Immediately"), 0), (Loc.Mark("0.5 seconds"), 500), (Loc.Mark("1 second"), 1000), (Loc.Mark("1.5 seconds"), 1500), (Loc.Mark("2 seconds"), 2000), (Loc.Mark("3 seconds"), 3000), (Loc.Mark("5 seconds"), 5000)];

    internal void ShowOverlaySaveError(string error) => SessionHint.Text = error;

    private void Preview_Click(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke(this, EventArgs.Empty);
    private async void Close_Click(object sender, RoutedEventArgs e) => await RequestCloseAsync();

    private async Task RequestCloseAsync()
    {
        if (!await ConfirmLeaveIntegrationAsync()) return;
        await ShutdownSetupImportAsync();
        IntegrationDismissed?.Invoke();
        _allowClose = true;
        Close();
    }
    private void CustomizeLayout_Click(object sender, RoutedEventArgs e) => ShowCategory("Overlay editor");
    private void BackToAppearance_Click(object sender, RoutedEventArgs e) => ShowCategory("Appearance");
    internal void ShowSelectComparison()
    {
        ShowCategory("Appearance");
        PreviewDismissed?.Invoke();
        SettingsScroll.Visibility = EditorPreviewButton.Visibility = Visibility.Collapsed;
        ComparisonScroll.Visibility = Visibility.Visible;
        SessionHint.Text = "Design comparison only · tell me 1–4";
    }
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var recorder = Descendants(SettingsRoot).OfType<ShortcutRecorder>().FirstOrDefault(control => control.IsEditing);
        if (recorder is not null && (recorder.IsCapturing || e.Key == global::Windows.System.VirtualKey.Escape)) recorder.CaptureKeyDown(e);
    }

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e) =>
        Descendants(SettingsRoot).OfType<ShortcutRecorder>().FirstOrDefault(control => control.IsCapturing)?.CaptureKeyUp(e);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (SetupHost.Child is SetupWizard wizard)
        {
            if (e.Key == global::Windows.System.VirtualKey.Escape)
            {
                if (!wizard.CloseOpenPicker()) ExitSetup(false);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == global::Windows.System.VirtualKey.F &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            SettingsSearch.Focus(FocusState.Keyboard);
            SettingsSearch.SelectAll();
            e.Handled = true;
            return;
        }
        if (WorkspaceHost.Visibility == Visibility.Visible)
        {
            WorkspaceKey?.Invoke(_currentCategory, e);
            if (e.Handled) return;
            if (e.Key == global::Windows.System.VirtualKey.Escape && WorkspaceBack?.Invoke(_currentCategory) == true)
            {
                e.Handled = true;
                return;
            }
        }
        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            if ((_currentCategory == "Integrations" || _currentCategory.StartsWith("plugin:", StringComparison.Ordinal)) && !_searchActive && NavigateIntegrationBack?.Invoke() == true)
            {
                e.Handled = true;
                return;
            }
            if (ComparisonScroll.Visibility == Visibility.Visible)
            {
                if (!SelectComparison.CloseOpenPicker()) ShowCategory("Appearance");
                e.Handled = true;
                return;
            }
            var picker = _catalogPickers.Concat(_appearancePickers).FirstOrDefault(p => p.IsPopupOpen);
            if (picker is not null) picker.ClosePopup();
            else if (Descendants(CatalogContent).OfType<SyncBackupView>().FirstOrDefault()?.ClosePreview() == true) { }
            else if (OverlayEditor.CloseOpenPicker()) { }
            else if (SettingsSearch.Text.Length > 0) ClearSearch_Click(this, new RoutedEventArgs());
            else Close_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    internal async void ShowCategory(string category) => await TryShowCategoryAsync(category);

    private async Task<bool> ConfirmLeaveIntegrationAsync()
    {
        if (_checkingNavigation) return false;
        _checkingNavigation = true;
        try { return CanLeaveIntegrationAsync is null || await CanLeaveIntegrationAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { SessionHint.Text = Loc.T("Could not leave this page. Save your profile changes and try again."); return false; }
        finally { _checkingNavigation = false; }
    }

    private async Task<bool> TryShowCategoryAsync(string category)
    {
        if (_checkingNavigation || category != _currentCategory && !await ConfirmLeaveIntegrationAsync()) return false;
        ShowCategoryCore(category);
        return true;
    }

    private void ShowCategoryCore(string category)
    {
        if (category is not ("Appearance" or "Overlay editor")) PreviewDismissed?.Invoke();
        // TextChanged can arrive after the programmatic clear. It must not rebuild
        // this page again and remove the control focused by OpenSearchResult.
        _searchActive = false;
        _currentCategory = category;
        _changingSearch = true;
        SettingsSearch.Text = "";
        _changingSearch = false;
        SearchPlaceholder.Visibility = Visibility.Visible;
        ClearSettingsSearch.Visibility = Visibility.Collapsed;
        _searchButtons.Clear();
        ComparisonScroll.Visibility = Visibility.Collapsed;
        // Status notes belong to the page that set them.
        SessionHint.Text = "";
        foreach (var button in _navigationButtons)
            SetNavigationSelected(button, (string)button.Tag == (category == "Overlay editor" ? "Appearance" : category));
        SettingsScroll.Visibility = category == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        EditorScroll.Visibility = category == "Overlay editor" ? Visibility.Visible : Visibility.Collapsed;
        if (category == "Overlay editor") RefreshPreviewSize();
        var integration = category == "Integrations" || category.StartsWith("plugin:", StringComparison.Ordinal);
        if (!integration) IntegrationDismissed?.Invoke();
        IntegrationsHost.Visibility = integration ? Visibility.Visible : Visibility.Collapsed;
        var workspace = WorkspaceCategories.Contains(category);
        WorkspaceHost.Child = workspace ? WorkspacePage?.Invoke(category) : null;
        WorkspaceHost.Visibility = workspace ? Visibility.Visible : Visibility.Collapsed;
        // The dashboard draws its own full-width header band.
        WorkspaceHost.Margin = category == "Home" ? new Thickness(0) : new Thickness(12, 12, 12, 0);
        WorkspaceChanged?.Invoke(workspace ? category : null);
        if (WorkspaceHost.Child is RecorderView recorder) RenderRecorderDefaults(recorder.DefaultsPanel);
        var catalog = category != "Appearance" && category != "Overlay editor" && !integration && !workspace;
        CatalogScroll.Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
        EditorPreviewButton.Visibility = category == "Overlay editor" ? Visibility.Visible : Visibility.Collapsed;
        if (integration) IntegrationRequested?.Invoke(category == "Integrations" ? null : category[7..]);
        if (catalog)
        {
            _catalogPickers.Clear();
            SettingsCatalog.Render(category, CatalogContent, _values, _catalogPickers, () => ShowCategory(category), CommitDictationHotkeys, CommitCancelProcessingHotkeys, CommitRecentTranscriptionsHotkeys, CommitCopyLastTranscriptionHotkeys, CommitPasteLastTranscriptionHotkeys, CommitReadLastTranscriptionHotkeys, CommitWorkflowPaletteHotkeys, CommitRecordingShortcut, CommitRecorderHotkeys);
            ConfigureLiveSettings?.Invoke(category, CatalogContent, _catalogPickers);
            SettingsCatalog.UpdateTrailingSeparators(CatalogContent);
            if (category == "General")
            {
                var setup = new HandCursorButton { Content = Loc.T("Open setup wizard"), HorizontalAlignment = HorizontalAlignment.Left,
                    Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
                setup.Click += (_, _) => ShowSetup();
                CatalogContent.Children.Add(setup);
            }
            CatalogScroll.ChangeView(null, 0, null, true);
        }
    }

    // Selected rows are filled with the accent color, as in the macOS sidebar.
    internal static void SetNavigationSelected(HandCursorButton button, bool selected)
    {
        button.Style = (Style)Application.Current.Resources[selected ? "SidebarSelectedButtonStyle" : "MenuButtonStyle"];
        AutomationProperties.SetItemStatus(button, selected ? Loc.T("Selected") : Loc.T("Not selected"));
        if (button.Content is not Panel row) return;
        foreach (var child in row.Children)
        {
            if (child is TypeWhisperGlyph glyph) glyph.Inverse = selected;
            else if (child is TextBlock label) label.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    // The footer only appears for a status message or a page action.
    private void UpdateFooter() => SettingsFooter.Visibility =
        SetupHost.Child is null && (SessionHint.Text.Length > 0 || EditorPreviewButton.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    private void RenderRecorderDefaults(StackPanel panel)
    {
        _catalogPickers.Clear();
        SettingsCatalog.Render("Recorder", panel, _values, _catalogPickers, () => ShowCategory("Recorder"));
        ConfigureLiveSettings?.Invoke("Recorder", panel, _catalogPickers);
        // Replace the page title with a section heading below the session controls.
        // Live settings wrap their controls, including the title, in one child panel.
        var titled = panel.Children is [StackPanel live] ? live : panel;
        if (titled.Children.Count > 0) titled.Children.RemoveAt(0);
        panel.Children.Insert(0, new TextBlock { Text = Loc.T("Defaults"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        SettingsCatalog.UpdateTrailingSeparators(panel);
    }

    internal void ShowRecoveryFromTray(bool allowNavigation)
    {
        if (_currentCategory == "Files & recovery") return;
        if (allowNavigation) ShowCategory("Files & recovery");
        else SessionHint.Text = Loc.T("Open Files & recovery from the sidebar to review saved audio. Your current settings are kept intact.");
    }
    internal void ShowAccount() => ShowCategory("Account & about");
    // The setup wizard covers the settings; it stays in front until the user finishes or leaves it.
    internal void ShowSetting(string category, string key)
    {
        if (SetupHost.Child is null) OpenSearchResult(new(category, key, "", "", ""));
    }


    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ShowCategory(_currentCategory);
        SettingsSearch.Focus(FocusState.Keyboard);
    }

    private bool _returnToTrayAfterSetup;
    internal Task ShutdownSetupImportAsync() => SetupHost.Child is SetupWizard wizard ? wizard.ShutdownImportAsync() : Task.CompletedTask;

    internal void ShowSetup(bool returnToTray = false)
    {
        if (CreateSetupWizard is null) return;
        PreviewDismissed?.Invoke();
        _returnToTrayAfterSetup = returnToTray;
        foreach (var picker in _catalogPickers.Concat(_appearancePickers)) if (picker.IsPopupOpen) picker.ClosePopup();
        CatalogContent.Children.Clear(); _catalogPickers.Clear();
        SettingsBrand.Visibility = SettingsBody.Visibility = SettingsFooter.Visibility = Visibility.Collapsed;
        SetupHost.Child = CreateSetupWizard(ExitSetup);
        SetupHost.Visibility = Visibility.Visible;
    }

    private void ExitSetup(bool completed)
    {
        SetupHost.Child = null; SetupHost.Visibility = Visibility.Collapsed;
        if (_returnToTrayAfterSetup) { _returnToTrayAfterSetup = false; Close(); return; }
        SettingsBrand.Visibility = SettingsBody.Visibility = Visibility.Visible;
        UpdateFooter();
        ShowCategory(completed ? "Dictation" : "General");
    }

    private void SettingsSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Down && _searchButtons.Count > 0)
        {
            _searchButtons[0].Focus(FocusState.Keyboard);
            e.Handled = true;
        }
        else if (e.Key == global::Windows.System.VirtualKey.Enter && _searchButtons.FirstOrDefault()?.Tag is SettingSearchEntry entry)
        {
            OpenSearchResult(entry);
            e.Handled = true;
        }
    }

    private static readonly SettingSearchEntry[] AppearanceSearchEntries =
    [
        new("Appearance", "StandardChoice", Loc.T("Recording overlay"), Loc.T("Choose Standard, Compact or Minimal."), "microphone", "waveform indicator"),
        new("Appearance", "LiveTextToggle", Loc.T("Live transcription"), Loc.T("Show streaming text beside the recording block."), "text"),
        new("Appearance", "DetailsToggle", Loc.T("Technical details"), Loc.T("Show audio level and render frequency."), "signal", "dB FPS"),
        new(Loc.Mark("Overlay editor"), "", Loc.T("Customize layout"), Loc.T("Choose screen position and arrange the left and right widgets."), "layout", "appearance monitor top bottom drag"),
        new(Loc.Mark("Integrations"), "", Loc.T("Integrations"), Loc.T("Manage installed plugins, accounts, models and updates."), "plugin", "OpenAI ChatGPT Groq ElevenLabs API key login discover marketplace"),
        new("Advanced", "", Loc.T("HTTP API"), Loc.T("Connect local scripts and apps, configure the port, and copy the API token."), "settings", "advanced server localhost auto-discovery automation"),
        new("Advanced", DiagnosticsSettingsView.SettingKey, Loc.T("Diagnostics"), Loc.T("Keep a local log without dictated text and export it for support."), "settings", "error log crash troubleshooting support export retention"),
        new("Premium", "", Loc.T("Premium"), Loc.T("Premium access, commercial license and development activation."), "lock", "supporter calendar correction learning cloud sync"),
        new("Account & about", "", Loc.T("Account & about"), Loc.T("License, Premium, updates and app information."), "info"),
        new("Home", "", Loc.T("Home"), Loc.T("Recent activity and transcriptions."), "home", "dashboard overview start"),
        new("Workflows", "", Loc.T("Workflows"), Loc.T("Create and edit workflows, triggers and their LLM."), "workflow", "prompt template automation selected text"),
        new("Dictionary", "", Loc.T("Dictionary"), Loc.T("Your words, corrections and term packs."), "dictionary", "vocabulary words corrections spelling"),
        new("Snippets", "", Loc.T("Snippets"), Loc.T("Reusable text with spoken triggers."), "text", "text expansion shortcut"),
        new("Recorder", "", Loc.T("Recorder"), Loc.T("Record microphone and system audio."), "recorder", "meeting session recordings"),
        new("File transcription", "", Loc.T("File transcription"), Loc.T("Transcribe audio and video files or watch a folder."), "file", "import audio video watch folder queue"),
        new("Statistics", "", Loc.T("Statistics"), Loc.T("Words, apps and models over time."), "stats", "activity usage streaks"),
        new("Sync & backup", "", Loc.T("Sync & backup"), Loc.T("Back up, restore, export or delete your data."), "devices", "backup restore export delete data")
    ];

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_changingSearch || SearchPlaceholder is null || CatalogContent is null) return;
        var query = SettingsSearch.Text;
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSettingsSearch.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (string.IsNullOrWhiteSpace(query))
        {
            if (_searchActive) ShowCategory(_currentCategory);
            return;
        }
        PreviewDismissed?.Invoke();
        _searchActive = true;
        IntegrationsHost.Visibility = Visibility.Collapsed;
        WorkspaceHost.Child = null;
        WorkspaceHost.Visibility = Visibility.Collapsed;
        WorkspaceChanged?.Invoke(null);
        SettingsScroll.Visibility = EditorScroll.Visibility = ComparisonScroll.Visibility = EditorPreviewButton.Visibility = Visibility.Collapsed;
        CatalogScroll.Visibility = Visibility.Visible;
        _catalogPickers.Clear();
        _searchButtons.Clear();
        CatalogContent.Children.Clear();
        foreach (var button in _navigationButtons) SetNavigationSelected(button, false);
        var results = SettingsSearchIndex.Find(SettingsCatalog.SearchEntries.Concat(AppearanceSearchEntries), query);
        CatalogContent.Children.Add(SearchText(Loc.T("Search settings"), 24));
        CatalogContent.Children.Add(SearchText(results.Count == 0 ? Loc.T("No matching settings. Try a shorter term, such as microphone, language or overlay.")
            : results.Count == 1 ? Loc.T("{0} matching setting · select one to open its page", results.Count)
            : Loc.T("{0} matching settings · select one to open its page", results.Count), 13, true));
        foreach (var result in results)
        {
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TypeWhisperGlyph { Kind = result.Icon, Width = 18, Height = 18 });
            var copy = new StackPanel { Spacing = 5 };
            copy.Children.Add(SearchText(result.Label, 14));
            copy.Children.Add(SearchText(result.Category == "Overlay editor" ? Loc.T("Appearance · Layout") : DisplayName(result.Category), 11, true));
            if (result.Description.Length > 0) copy.Children.Add(SearchText(result.Description, 12, true));
            Grid.SetColumn(copy, 1); row.Children.Add(copy);
            var arrow = SearchText("→", 16, true); arrow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(arrow, 2); row.Children.Add(arrow);
            var button = new HandCursorButton { Content = row, Tag = result, Padding = new Thickness(14),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["MenuButtonStyle"] };
            AutomationProperties.SetName(button, Loc.T("Open {0} in {1}", result.Label, DisplayName(result.Category)));
            button.Click += (_, _) => OpenSearchResult(result);
            button.KeyDown += (_, key) =>
            {
                if (key.Key is not (global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)) return;
                var index = _searchButtons.IndexOf(button) + (key.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
                if (index < 0) SettingsSearch.Focus(FocusState.Keyboard);
                else _searchButtons[Math.Min(index, _searchButtons.Count - 1)].Focus(FocusState.Keyboard);
                key.Handled = true;
            };
            _searchButtons.Add(button); CatalogContent.Children.Add(button);
        }
        CatalogScroll.ChangeView(null, 0, null, true);
    }

    private static TextBlock SearchText(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = muted ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };

    private async void OpenSearchResult(SettingSearchEntry entry)
    {
        if (!await TryShowCategoryAsync(entry.Category)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            SettingsRoot.UpdateLayout();
            var elements = Descendants(SettingsRoot).ToArray();
            var target = elements.FirstOrDefault(element => entry.Key.Length > 0 && (element.Name == entry.Key || element.Tag as string == entry.Key));
            for (DependencyObject? ancestor = target; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is FrameworkElement { Tag: Action reveal }) reveal();
            SettingsRoot.UpdateLayout();
            var visible = target is not null && IsVisible(target);
            if (!visible)
            {
                // Hidden dependent fields lead to their enabling option without changing it.
                var parentKey = entry.Key switch
                {
                    "AudioDuckingLevel" => "AudioDuckingEnabled",
                    "SpokenFeedbackVoiceId" or "SpokenFeedbackProviderId" => "SpokenFeedbackEnabled",
                    "SilenceAutoStopSeconds" => "SilenceAutoStopEnabled",
                    "TranslationTargetLanguage" => "TranscriptionTask",
                    "LanguageHints" => "Language",
                    "LockPasteToFocusedField" => "AutoPaste",
                    "VocabularyBoostingEnabledPackIds" or "VocabularyBoostingSelectedIndustryPresetId" => "VocabularyBoostingEnabled",
                    _ => ""
                };
                target = elements.FirstOrDefault(element => parentKey.Length > 0 && element.Tag as string == parentKey);
            }
            if (target is not null)
            {
                target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.25 });
                if (target is Control control) control.Focus(FocusState.Keyboard);
                else Descendants(target).OfType<Control>().FirstOrDefault(control => control is Button or TextBox or ToggleSwitch && control.IsTabStop && control.IsEnabled)?.Focus(FocusState.Keyboard);
            }
            else _navigationButtons.FirstOrDefault(button => (string)button.Tag == (entry.Category == "Overlay editor" ? "Appearance" : entry.Category))?.Focus(FocusState.Keyboard);
        });
    }

    private static bool IsVisible(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
