using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.Graphics;
using global::Windows.UI;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeSettingsWindow : Window
{
    private PrototypeOverlayPreferences _preferences;
    internal Func<string, string?>? CommitLauncherHotkeys { get; set; }
    internal Func<string, string?>? CommitDictationHotkeys { get; set; }
    private bool _updating = true;
    private uint _dpi;
    private bool _positioning;
    private bool _changingSearch;
    private bool _searchActive;
    private string _currentCategory = "Appearance";
    private readonly List<HandCursorButton> _searchButtons = [];
    internal event Action<PrototypeOverlayPreferences>? PreferencesChanged;
    internal event EventHandler? PreviewRequested;

    private readonly Dictionary<string, string> _values;
    private readonly List<PrototypeChoicePicker> _catalogPickers = [];
    private readonly List<PrototypeChoicePicker> _appearancePickers = [];
    private readonly List<HandCursorButton> _navigationButtons = [];
    private PrototypeActivityView? _activity;
    internal event Action? HistoryRequested;
    internal PrototypeSettingsWindow(PrototypeOverlayPreferences preferences, Dictionary<string, string> values)
    {
        _values = values;
        _preferences = preferences;
        InitializeComponent();
        PrototypeToggleSwitch.Configure(LiveTextToggle);
        PrototypeToggleSwitch.Configure(DetailsToggle);
        OverlayEditor.Changed += Publish;
        (string Heading, (string Category, string Icon)[] Items)[] groups =
        [
            ("APP", [("Home", "home"), ("General", "settings"), ("Shortcuts", "keyboard")]),
            ("RECORDING", [("Dictation", "microphone"), ("Audio", "speaker"), ("Recorder", "signal"), ("Files & recovery", "file"), ("Models", "chip")]),
            ("PERSONALIZATION", [("Appearance", "desktop")]),
            ("DATA & SYSTEM", [("Statistics", "stats"), ("Privacy", "lock"), ("Sync & backup", "devices"), ("Automation", "workflow"), ("Account & about", "info")])
        ];
        foreach (var group in groups)
        {
            var section = new StackPanel { Spacing = 2 };
            section.Children.Add(new TextBlock { Text = group.Heading, FontSize = 10, CharacterSpacing = 70,
                Foreground = (Brush)Application.Current.Resources["MutedBrush"], Margin = new Thickness(10, 0, 0, 6) });
            foreach (var (category, icon) in group.Items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 18, Height = 18 });
                row.Children.Add(new TextBlock { Text = category, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
                var button = new HandCursorButton { Content = row, Tag = category, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 34, Padding = new Thickness(10, 7, 10, 7),
                    Style = (Style)Application.Current.Resources["PrototypeMenuButtonStyle"] };
                AutomationProperties.SetName(button, $"Settings category {category}");
                button.Click += (_, _) => ShowCategory(category);
                section.Children.Add(button);
                _navigationButtons.Add(button);
            }
            SettingsNavigation.Children.Add(section);
        }
        PrototypeSettingsCatalog.RenderLiveTextOptions(LiveTextOptions, _values, _appearancePickers);
        ShowCategory("Appearance");
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
            else foreach (var recorder in Descendants(SettingsRoot).OfType<PrototypeShortcutRecorder>()) recorder.Cancel(false);
        };
        AppWindow.Changed += (_, args) =>
        {
            if (_positioning || !args.DidPositionChange) return;
            var currentDpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (currentDpi != _dpi) PlaceOn(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
        };
        SetPreferences(preferences);
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
            _dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
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

    internal void SetPreferences(PrototypeOverlayPreferences preferences)
    {
        _updating = true;
        _preferences = preferences;
        OverlayEditor.SetPreferences(preferences);
        foreach (var button in new[] { StandardChoice, CompactChoice, MinimalChoice })
        {
            var selected = (string)button.Tag == preferences.Mode.ToString();
            button.Style = (Style)Application.Current.Resources[selected ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"];
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        }
        LiveTextToggle.IsOn = preferences.LiveText;
        DetailsToggle.IsOn = preferences.TechnicalDetails;
        var minimal = preferences.Mode == PrototypeOverlayMode.Minimal;
        var standard = preferences.Mode == PrototypeOverlayMode.Standard;
        LiveTextToggle.IsEnabled = !minimal;
        DetailsToggle.IsEnabled = standard;
        LiveTextDescription.Text = minimal
            ? "Hidden in Minimal. Your preference is kept for Standard and Compact."
            : "Show streaming text beside the recording block. Longer text scrolls.";
        DetailsDescription.Text = standard
            ? "Show the audio level in dBFS and measured render frequency. Off by default."
            : "Available in Standard only. Your preference is kept when switching layouts.";
        _updating = false;
    }

    internal void SetPreviewVisible(bool visible) => PreviewButton.Content = visible ? "Stop preview" : "Preview overlay";

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode } || !Enum.TryParse<PrototypeOverlayMode>(mode, out var selected)) return;
        Publish(_preferences with { Mode = selected });
    }

    private void Preference_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        Publish(_preferences with { LiveText = LiveTextToggle.IsOn, TechnicalDetails = DetailsToggle.IsOn });
    }

    private void Publish(PrototypeOverlayPreferences preferences)
    {
        SetPreferences(preferences);
        PreferencesChanged?.Invoke(preferences);
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke(this, EventArgs.Empty);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void CustomizeLayout_Click(object sender, RoutedEventArgs e) => ShowCategory("Overlay editor");
    private void BackToAppearance_Click(object sender, RoutedEventArgs e) => ShowCategory("Appearance");
    internal void ShowSelectComparison()
    {
        ShowCategory("Appearance");
        SettingsScroll.Visibility = PreviewButton.Visibility = Visibility.Collapsed;
        ComparisonScroll.Visibility = Visibility.Visible;
        SessionHint.Text = "Design comparison only · tell me 1–4";
    }
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var recorder = Descendants(SettingsRoot).OfType<PrototypeShortcutRecorder>().FirstOrDefault(control => control.IsEditing);
        if (recorder is not null && (recorder.IsCapturing || e.Key == global::Windows.System.VirtualKey.Escape)) recorder.CaptureKeyDown(e);
    }

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e) =>
        Descendants(SettingsRoot).OfType<PrototypeShortcutRecorder>().FirstOrDefault(control => control.IsCapturing)?.CaptureKeyUp(e);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (SetupHost.Child is PrototypeSetupWizard wizard)
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
        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            if (_activity?.CloseRangeIfOpen() == true) { e.Handled = true; return; }
            if (ComparisonScroll.Visibility == Visibility.Visible)
            {
                if (!SelectComparison.CloseOpenPicker()) ShowCategory("Appearance");
                e.Handled = true;
                return;
            }
            var picker = _catalogPickers.Concat(_appearancePickers).FirstOrDefault(p => p.IsPopupOpen);
            if (picker is not null) picker.ClosePopup();
            else if (Descendants(CatalogContent).OfType<PrototypeSyncBackupView>().FirstOrDefault()?.ClosePreview() == true) { }
            else if (OverlayEditor.CloseOpenPicker()) { }
            else if (SettingsSearch.Text.Length > 0) ClearSearch_Click(this, new RoutedEventArgs());
            else Close();
            e.Handled = true;
        }
    }

    private void ShowCategory(string category)
    {
        ActivityHost.Visibility = Visibility.Collapsed;
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
        foreach (var button in _navigationButtons)
        {
            var selected = (string)button.Tag == (category == "Overlay editor" ? "Appearance" : category);
            button.Style = (Style)Application.Current.Resources[selected ? "PrototypePrimaryButtonStyle" : "PrototypeMenuButtonStyle"];
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        }
        SettingsScroll.Visibility = category == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        EditorScroll.Visibility = category == "Overlay editor" ? Visibility.Visible : Visibility.Collapsed;
        var catalog = category != "Appearance" && category != "Overlay editor";
        CatalogScroll.Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Visibility = catalog ? Visibility.Collapsed : Visibility.Visible;
        SessionHint.Text = catalog ? "UI preview only · no system changes" : "Applies to preview · this session only";
        if (catalog)
        {
            _catalogPickers.Clear();
            if (category is "Home" or "Statistics")
            {
                CatalogContent.Children.Clear(); CatalogScroll.Visibility = Visibility.Collapsed;
                if (_activity is null)
                {
                    _activity = new PrototypeActivityView();
                    _activity.NavigateRequested += destination =>
                    {
                        if (destination == "Setup") ShowSetup();
                        else if (destination == "History") HistoryRequested?.Invoke();
                        else ShowCategory(destination);
                    };
                    ActivityHost.Child = _activity;
                }
                ActivityHost.Visibility = Visibility.Visible; _activity.Present(category == "Statistics");
                SessionHint.Text = "Sample activity only · no personal usage is measured";
                return;
            }
            PrototypeSettingsCatalog.Render(category, CatalogContent, _values, _catalogPickers, () => ShowCategory(category), CommitLauncherHotkeys, CommitDictationHotkeys);
            if (category == "General")
            {
                var setup = new HandCursorButton { Content = "Open setup wizard", HorizontalAlignment = HorizontalAlignment.Left,
                    Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
                setup.Click += (_, _) => ShowSetup();
                CatalogContent.Children.Add(setup);
            }
            CatalogScroll.ChangeView(null, 0, null, true);
        }
    }

    internal void ShowActivity(bool statistics) => ShowCategory(statistics ? "Statistics" : "Home");
    internal void ShowSyncBackup() => ShowCategory("Sync & backup");
    internal void ShowAccount() => ShowCategory("Account & about");
    internal void ShowHistoryNavigationHint() => SessionHint.Text = "Return to Quick Launch first to open History. Your current workspace is kept intact.";

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ShowCategory(_currentCategory);
        SettingsSearch.Focus(FocusState.Keyboard);
    }

    internal void ShowSetup()
    {
        foreach (var picker in _catalogPickers.Concat(_appearancePickers)) if (picker.IsPopupOpen) picker.ClosePopup();
        CatalogContent.Children.Clear(); _catalogPickers.Clear();
        SettingsBody.Visibility = SettingsFooter.Visibility = Visibility.Collapsed;
        SetupHost.Child = new PrototypeSetupWizard(_values, ExitSetup, CommitDictationHotkeys);
        SetupHost.Visibility = Visibility.Visible;
    }

    private void ExitSetup(bool completed)
    {
        SetupHost.Child = null; SetupHost.Visibility = Visibility.Collapsed;
        SettingsBody.Visibility = SettingsFooter.Visibility = Visibility.Visible;
        ShowCategory(completed ? "Dictation" : "General");
    }

    private void SettingsSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Down && _searchButtons.Count > 0)
        {
            _searchButtons[0].Focus(FocusState.Keyboard);
            e.Handled = true;
        }
        else if (e.Key == global::Windows.System.VirtualKey.Enter && _searchButtons.FirstOrDefault()?.Tag is PrototypeSettingSearchEntry entry)
        {
            OpenSearchResult(entry);
            e.Handled = true;
        }
    }

    private static readonly PrototypeSettingSearchEntry[] AppearanceSearchEntries =
    [
        new("Home", "", "Dashboard", "Your activity and recent transcriptions.", "home", "start overview"),
        new("Statistics", "", "Usage statistics", "Words, streaks, apps, models, and hourly activity.", "stats", "week month time saved"),
        new("Appearance", "StandardChoice", "Recording overlay", "Choose Standard, Compact or Minimal.", "microphone", "waveform indicator"),
        new("Appearance", "LiveTextToggle", "Live transcription", "Show streaming text beside the recording block.", "text"),
        new("Appearance", "DetailsToggle", "Technical details", "Show audio level and render frequency.", "signal", "dB FPS"),
        new("Overlay editor", "", "Customize layout", "Choose screen position and arrange the left and right widgets.", "layout", "appearance monitor top bottom drag"),
        new("Account & about", "", "Account & about", "License, Premium, updates and app information.", "info")
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
        _searchActive = true;
        ActivityHost.Visibility = Visibility.Collapsed;
        SettingsScroll.Visibility = EditorScroll.Visibility = ComparisonScroll.Visibility = PreviewButton.Visibility = Visibility.Collapsed;
        CatalogScroll.Visibility = Visibility.Visible;
        _catalogPickers.Clear();
        _searchButtons.Clear();
        CatalogContent.Children.Clear();
        foreach (var button in _navigationButtons)
        {
            button.Style = (Style)Application.Current.Resources["PrototypeMenuButtonStyle"];
            AutomationProperties.SetItemStatus(button, "Not selected");
        }
        var results = PrototypeSettingsSearch.Find(PrototypeSettingsCatalog.SearchEntries.Concat(AppearanceSearchEntries), query);
        CatalogContent.Children.Add(SearchText("Search settings", 24));
        CatalogContent.Children.Add(SearchText(results.Count == 0 ? "No matching settings. Try a shorter term, such as microphone, language or overlay."
            : $"{results.Count} matching {(results.Count == 1 ? "setting" : "settings")} · select one to open its page", 13, true));
        foreach (var result in results)
        {
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TypeWhisperGlyph { Kind = result.Icon, Width = 18, Height = 18 });
            var copy = new StackPanel { Spacing = 5 };
            copy.Children.Add(SearchText(result.Label, 14));
            copy.Children.Add(SearchText(result.Category == "Overlay editor" ? "Appearance · Layout" : result.Category, 11, true));
            if (result.Description.Length > 0) copy.Children.Add(SearchText(result.Description, 12, true));
            Grid.SetColumn(copy, 1); row.Children.Add(copy);
            var arrow = SearchText("→", 16, true); arrow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(arrow, 2); row.Children.Add(arrow);
            var button = new HandCursorButton { Content = row, Tag = result, Padding = new Thickness(14),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["PrototypeMenuButtonStyle"] };
            AutomationProperties.SetName(button, $"Open {result.Label} in {result.Category}");
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
        SessionHint.Text = "Search all settings · Esc clears · Enter opens";
        CatalogScroll.ChangeView(null, 0, null, true);
    }

    private static TextBlock SearchText(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = muted ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };

    private void OpenSearchResult(PrototypeSettingSearchEntry entry)
    {
        ShowCategory(entry.Category);
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
