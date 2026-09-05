using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow : Window
{
    private const int CompactWidth = 780;
    private const int CompactHeight = 520;
    private static string LauncherHotkeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "quick-launch-hotkeys.txt");

    private string? ChangeLauncherHotkeys(string value)
    {
        if (_hotkeyRegistration is null) return "Global hotkey service is unavailable. Restart the app.";
        var previous = _hotkeyRegistration.Value;
        var error = _hotkeyRegistration.TryChange(value);
        if (error is not null) return error;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherHotkeyPath)!);
            File.WriteAllText(LauncherHotkeyPath, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollbackError = _hotkeyRegistration.TryChange(previous);
            HotkeyHint.Text = _hotkeyRegistration.DisplayText;
            return rollbackError ?? $"Could not save the shortcut: {ex.Message}";
        }
        HotkeyHint.Text = _hotkeyRegistration.DisplayText;
        return null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    private static readonly IReadOnlyList<PrototypeCommand> Commands =
    [
        new("Pinned", "microphone", "Dictation", "Focus a text field, then use your dictation shortcut", "", "Configure Main dictation in Settings > Shortcuts. Use the shortcut to start, and again to finish."),
        new("Pinned", "history", "History", "Browse, search, copy, and export transcriptions", "H", "Opens History in workspace mode. Full transcript search remains inside this explicit scope."),
        new("Pinned", "recorder", "Recorder", "Record microphone and system audio", "R", "Opens the recorder workspace without interrupting active dictation."),
        new("Pinned", "workflow", "Workflows", "Run and manage reusable text workflows", "W", "Choose a workflow, inspect its provider, and run it against selected or dictated text."),
        new("Suggested", "plugin", "Plugins", "Manage integrations and plugin settings", "", "Shows installed plugins, their health, permissions, settings, and updates."),
        new("Suggested", "market", "Marketplace", "Discover signed TypeWhisper plugins", "", "Browse verified plugins without leaving Quick Launch."),
        new("Suggested", "settings", "Settings", "Audio, hotkeys, privacy, account, and updates", "Ctrl ,", "Opens the dedicated Settings surface for global application configuration."),
        new("Suggested", "file", "Transcribe file", "Drop or choose audio and video files", "", "Opens the file transcription queue in workspace mode."),
        new("Suggested", "dictionary", "Dictionary", "Your words and preferred spellings", "D", "Manage words and correction rules used by TypeWhisper."),
        new("Suggested", "text", "Snippets", "Reusable text with spoken triggers", "", "Create and edit text snippets."),
        new("Suggested", "home", "Dashboard", "Your activity and recent transcriptions", "", "Opens the activity dashboard."),
        new("Suggested", "stats", "Statistics", "Words, streaks, apps, and models", "", "Explore your usage over time."),
    ];

    internal ObservableCollection<PrototypeCommand> FilteredItems { get; } = [];

    private readonly Stopwatch _activationStopwatch = Stopwatch.StartNew();
    private readonly PrototypeHotkeyRegistration? _hotkeyRegistration;
    private DictationHotkeyRegistration? _dictationHotkey;
    private static string DictationHotkeyPath => Path.Combine(Path.GetDirectoryName(LauncherHotkeyPath)!, "dictation-hotkeys.txt");
    private string? ChangeDictationHotkeys(string value)
    {
        if (_dictationHotkey is null) return "Dictation hotkeys are unavailable. Restart the app.";
        if (_dictation.IsRecording) return "Finish the recording before changing its shortcut.";
        var previous = _dictationHotkey.Value;
        var error = _dictationHotkey.TryChange(value);
        if (error is not null) return error;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DictationHotkeyPath)!);
            File.WriteAllText(DictationHotkeyPath + ".tmp", _dictationHotkey.Value);
            File.Move(DictationHotkeyPath + ".tmp", DictationHotkeyPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollback = _dictationHotkey.TryChange(previous);
            return rollback ?? $"Could not save the shortcut: {ex.Message}";
        }
        _dictation.Shortcut = string.IsNullOrEmpty(_dictationHotkey.Value) ? "No shortcut assigned" : _dictationHotkey.Value;
        return null;
    }
    private readonly LocalDictationSession _dictation;
    private OverlayWindow? _liveOverlay;
    internal event Action<string, bool>? DictationChanged;

    internal async Task InitializeDictationAsync()
    {
        try
        {
            _dictationHotkey = new DictationHotkeyRegistration(this, action =>
            {
                _ = action switch
                {
                    HybridHotkeyAction.Start => _dictation.StartAsync(),
                    HybridHotkeyAction.Stop => _dictation.StopAsync(),
                    HybridHotkeyAction.Cancel => _dictation.CancelAsync(),
                    _ => _dictation.ToggleAsync()
                };
            }, () => _dictation.IsRecording);
            var saved = File.Exists(DictationHotkeyPath) ? File.ReadAllText(DictationHotkeyPath) : "Ctrl+Shift+F9";
            var error = _dictationHotkey.TryChange(saved);
            _settingsValues["MainDictationHotkeys"] = _dictationHotkey.Value;
            _dictation.Shortcut = string.IsNullOrEmpty(_dictationHotkey.Value) ? "No shortcut assigned" : _dictationHotkey.Value;
            if (error is not null) { MetricsText.Text = error; DictationChanged?.Invoke(error, false); return; }
            await _dictation.InitializeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { MetricsText.Text = "Dictation startup failed: " + ex.Message; }
    }

    internal void FinishDictationFromTray() { if (_dictation.IsRecording) _ = _dictation.ToggleAsync(); }
    internal void DisposeDictation() { _dictationHotkey?.Dispose(); _dictation.Dispose(); _liveOverlay?.Close(); }

    private void UpdateLiveDictation()
    {
        MetricsText.Text = _dictation.Status;
        DictationChanged?.Invoke(_dictation.Status, _dictation.IsRecording);
        if (_dictation.IsRecording)
        {
            if (_liveOverlay is null)
                _liveOverlay = new OverlayWindow(false, () => _dictation.CurrentLevel);
            _liveOverlay.SetLayout(OverlayPreferences);
            _liveOverlay.SetMode(_overlayMode, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
            _liveOverlay.ActivateWithoutTakingFocus();
        }
        else
        {
            _liveOverlay?.HidePreview();
            if (_historyOpen) _ = HistoryView.RefreshAsync();
        }
    }
    private PrototypeCommand? _selected;
    private OverlayWindow? _overlay;
    private PrototypeSettingsWindow? _settingsWindow;
    private bool _technicalDetailsEnabled;
    private bool _isSearchEditing;
    private bool _transcriptPreviewEnabled = true;
    private uint _appliedWindowDpi;
    private PrototypeOverlayMode _overlayMode = PrototypeOverlayMode.Standard;
    private bool _historyOpen;
    private bool _recorderOpen;
    private bool _workflowsOpen;
    private bool _pluginsOpen;
    private bool _marketplaceOpen;
    private string _launcherQuery = string.Empty;
    private PrototypeFileTranscriptionView? _fileTranscription;
    private bool FileTranscriptionOpen => FileTranscriptionHost.Visibility == Visibility.Visible;
    private PrototypeLexiconView? _lexicon;
    private bool LexiconOpen => LexiconHost.Visibility == Visibility.Visible;

    internal MainWindow()
    {
        InitializeComponent();
        var historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "history.json");
        var historyService = new TypeWhisper.Core.Services.HistoryService(historyPath) { ThrowOnLoadFailure = true };
        HistoryView.Connect(new TypeWhisper.Presentation.HistoryReader(historyService));
        _dictation = new LocalDictationSession(historyService, WinRT.Interop.WindowNative.GetWindowHandle(this));
        _dictation.Changed += () => DispatcherQueue.TryEnqueue(UpdateLiveDictation);
        HistoryView.ExitRequested += (_, _) => CloseHistory();
        RecorderView.ExitRequested += (_, _) => CloseRecorder();
        // Recorder previews must not be mixed into persisted history.
        RecorderView.OpenInHistoryRequested += id =>
        {
            CloseRecorder();
            OpenHistory();
            HistoryView.OpenEntry(id);
        };
        HistoryView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        WorkflowsView.ExitRequested += (_, _) => CloseWorkflows();
        WorkflowsView.LauncherRequested += (_, _) => { CloseWorkflows(); SearchBox.Text = string.Empty; };
        HistoryView.LauncherRequested += (_, _) => { CloseHistory(); SearchBox.Text = string.Empty; };
        RecorderView.LauncherRequested += (_, _) => { CloseRecorder(); SearchBox.Text = string.Empty; };
        WorkflowsView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        PluginsView.ExitRequested += (_, _) => ClosePlugins();
        PluginsView.LauncherRequested += (_, _) => { ClosePlugins(); SearchBox.Text = string.Empty; };
        PluginsView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        MarketplaceView.ConfigureInventory(PluginsView.ContainsPlugin, PluginsView.InstallSample);
        MarketplaceView.ExitRequested += (_, _) => CloseMarketplace();
        MarketplaceView.LauncherRequested += (_, _) => { CloseMarketplace(); SearchBox.Text = string.Empty; };
        MarketplaceView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        MarketplaceView.DetailModeChanged += detail =>
        {
            if (!_marketplaceOpen) return;
            SearchBox.IsEnabled = !detail;
            SearchSurface.IsHitTestVisible = !detail;
            SearchSurface.Opacity = detail ? 0.5 : 1;
        };
        MarketplaceView.ManageRequested += id =>
        {
            CloseMarketplace();
            SearchBox.Text = string.Empty;
            OpenPlugins();
            PluginsView.OpenEntry(id);
        };
        PluginsView.MarketplaceRequested += (_, _) =>
        {
            ClosePlugins();
            SearchBox.Text = string.Empty;
            OpenMarketplace();
        };
        PluginsView.DetailModeChanged += detail =>
        {
            if (!_pluginsOpen) return;
            SearchBox.IsEnabled = !detail;
            SearchSurface.IsHitTestVisible = !detail;
            SearchSurface.Opacity = detail ? 0.5 : 1;
        };
        WorkflowsView.ConfigurationModeChanged += editing =>
        {
            SearchBox.IsEnabled = !editing;
            SearchSurface.IsHitTestVisible = !editing;
            SearchSurface.Opacity = editing ? 0.5 : 1;
        };
        SearchSurface.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(SearchSurface_PointerPressed),
            handledEventsToo: true);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        NativeWindowAppearance.RemoveSystemBorder(this);
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };

        try
        {
            _hotkeyRegistration = new PrototypeHotkeyRegistration(this, ShowFromHotkey);
            var savedHotkey = File.Exists(LauncherHotkeyPath) ? File.ReadAllText(LauncherHotkeyPath) : "Alt+Space";
            var hotkeyError = _hotkeyRegistration.TryChange(savedHotkey);
            if (hotkeyError is not null) MetricsText.Text = hotkeyError;
            _settingsValues["QuickLaunchHotkeys"] = _hotkeyRegistration.Value;
            HotkeyHint.Text = _hotkeyRegistration.DisplayText;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MetricsDot.Fill = new SolidColorBrush(Colors.Orange);
            MetricsText.Text = $"Alt+Space unavailable · {exception.Message}";
        }

        foreach (var command in Commands)
            FilteredItems.Add(command);

        Activated += MainWindow_Activated;
        SizeChanged += (_, _) => PositionNearTopCenter();
        AppWindow.Changed += AppWindow_Changed;
        ResizeForCurrentMonitor();
        PlaceOnInvocationMonitor();
        SelectFirstResult();
    }

    internal void ShowFromActivation()
    {
        _activationStopwatch.Restart();
        _isSearchEditing = false;
        UpdateSearchPresentation();
        PlaceOnInvocationMonitor();
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
            presenter.IsAlwaysOnTop = true;
        }
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void ShowFromHotkey()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.IsVisible && GetForegroundWindow() == hwnd)
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = false;
            AppWindow.Hide();
            return;
        }
        ShowFromActivation();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
            return;
        }

        NativeWindowAppearance.RemoveSystemBorder(this);
        // Closing a settings picker reactivates the window. Keep its current
        // field focused instead of jumping back to the name and scrolling up.
        if (_workflowsOpen && WorkflowsView.IsConfiguring) return;
        if (_pluginsOpen && PluginsView.IsDetail) return;
        if (_marketplaceOpen && MarketplaceView.IsDetail) return;
        if (_recorderOpen) RecorderView.FocusEntry();
        else if (_workflowsOpen && WorkflowsView.IsDetail) WorkflowsView.FocusEntry();
        else SearchBox.Focus(FocusState.Programmatic);
        var elapsed = _activationStopwatch.Elapsed.TotalMilliseconds;
        MetricsText.Text = $"Interactive · {elapsed:0.0} ms activation";
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange)
            RecorderView.SetPresented(_recorderOpen && sender.IsVisible);
        if (!args.DidPositionChange)
            return;

        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (dpi == 0 || dpi == _appliedWindowDpi)
            return;

        ResizeForCurrentMonitor();
        PositionNearTopCenter();
    }

    private void ResizeForCurrentMonitor(RectInt32? targetWorkArea = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;

        _appliedWindowDpi = dpi;
        var logicalWidth = CompactWidth;
        var logicalHeight = CompactHeight;
        var scale = dpi / 96d;
        var width = (int)Math.Round(logicalWidth * scale);
        var height = (int)Math.Round(logicalHeight * scale);
        if (targetWorkArea is { } workArea)
        {
            var margin = (int)Math.Round(24 * scale);
            width = Math.Min(width, Math.Max(320, workArea.Width - margin * 2));
            height = Math.Min(height, Math.Max(360, workArea.Height - margin * 2));
        }
        AppWindow.Resize(new SizeInt32(
            width,
            height));
    }

    private void PlaceOnInvocationMonitor()
    {
        var area = ResolveInvocationDisplayArea();
        if (area is null)
            return;

        // Move the hidden window into the target work area first so Windows
        // reports that monitor's effective DPI before the final resize.
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + area.WorkArea.Width / 2,
            area.WorkArea.Y + 32));
        ResizeForCurrentMonitor(area.WorkArea);
        PositionNearTopCenter(area);
    }

    private DisplayArea? ResolveInvocationDisplayArea()
    {
        var ownWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero && foregroundWindow != ownWindow)
        {
            var foregroundId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(foregroundWindow);
            var foregroundArea = DisplayArea.GetFromWindowId(foregroundId, DisplayAreaFallback.None);
            if (foregroundArea is not null)
                return foregroundArea;
        }

        if (GetCursorPos(out var cursorPosition))
        {
            var cursorArea = DisplayArea.GetFromPoint(
                new PointInt32(cursorPosition.X, cursorPosition.Y),
                DisplayAreaFallback.None);
            if (cursorArea is not null)
                return cursorArea;
        }

        return DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
    }

    private void PositionNearTopCenter(DisplayArea? requestedArea = null)
    {
        var area = requestedArea ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null)
            return;

        var work = area.WorkArea;
        var size = AppWindow.Size;
        var x = work.X + Math.Max(0, (work.Width - size.Width) / 2);
        var scale = Math.Max(1d, _appliedWindowDpi / 96d);
        var y = work.Y + Math.Max((int)Math.Round(28 * scale), (work.Height - size.Height) / 7);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
            _isSearchEditing = true;
        UpdateSearchPresentation();

        if (_recorderOpen)
        {
            RecorderView.SessionTitle = SearchBox.Text;
            return;
        }

        if (_historyOpen)
        {
            HistoryView.Filter(query);
            return;
        }

        if (_workflowsOpen)
        {
            WorkflowsView.Filter(query);
            return;
        }

        if (_pluginsOpen)
        {
            PluginsView.Filter(query);
            return;
        }

        if (_marketplaceOpen)
        {
            MarketplaceView.Filter(query);
            return;
        }

        var matches = string.IsNullOrEmpty(query)
            ? Commands
            : Commands.Where(command =>
                command.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(command => string.Equals(command.Title, query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(command => command.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        FilteredItems.Clear();
        foreach (var command in matches)
            FilteredItems.Add(command);
        sw.Stop();

        CompactSectionLabel.Text = string.IsNullOrEmpty(query) ? "ACTIVE & PINNED" : $"{FilteredItems.Count} RESULTS";
        MetricsText.Text = $"Local search · {sw.Elapsed.TotalMilliseconds:0.00} ms · {FilteredItems.Count} results";
        MetricsDot.Fill = new SolidColorBrush(sw.Elapsed.TotalMilliseconds <= 16 ? Colors.MediumSeaGreen : Colors.OrangeRed);
        SelectFirstResult();
    }

    private void SearchSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginSearchEditing();
    }

    private void BeginSearchEditing()
    {
        _isSearchEditing = true;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Pointer);
        SearchBox.SelectionStart = SearchBox.Text.Length;
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SearchBox.Text))
            _isSearchEditing = false;
        UpdateSearchPresentation();
    }

    private void UpdateSearchPresentation()
    {
        if (SearchPlaceholder is null)
            return;

        var showPlaceholder = string.IsNullOrEmpty(SearchBox.Text) && !_isSearchEditing;
        SearchPlaceholder.Visibility = showPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Opacity = showPlaceholder ? 0 : 1;
    }

    private void SelectFirstResult()
    {
        if (FilteredItems.Count == 0)
        {
            UpdateDetail(null);
            return;
        }

        CompactResults.SelectedIndex = 0;
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: PrototypeCommand command })
        {
            _selected = command;
            UpdateDetail(command);
        }
    }

    private void UpdateDetail(PrototypeCommand? command)
    {
        _selected = command;
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PrototypeCommand command)
        {
            _selected = command;
            RunSelected();
        }
    }

    private void RunSelected()
    {
        if (FileTranscriptionOpen || LexiconOpen) return;
        ActionPanel.Visibility = Visibility.Collapsed;
        if (_recorderOpen) return;
        if (_marketplaceOpen)
            MarketplaceView.OpenSelected();
        else if (_pluginsOpen)
            PluginsView.OpenSelected();
        else if (_workflowsOpen)
            WorkflowsView.OpenSelected();
        else if (_historyOpen)
            HistoryView.OpenSelected();
        else if (_selected?.Title == "History")
            OpenHistory();
        else if (_selected?.Title == "Recorder")
            OpenRecorder();
        else if (_selected?.Title == "Workflows")
            OpenWorkflows();
        else if (_selected?.Title == "Plugins")
            OpenPlugins();
        else if (_selected?.Title == "Marketplace")
            OpenMarketplace();
        else if (_selected?.Title == "Transcribe file")
            OpenFileTranscription();
        else if (_selected?.Title == "Dictionary")
            OpenLexicon();
        else if (_selected?.Title == "Snippets")
            OpenLexicon(true);
        else if (_selected?.Title == "Settings")
            OpenSettings();
        else if (_selected?.Title is "Dashboard" or "Statistics")
            OpenDashboard(_selected.Title == "Statistics");
        else if (_selected?.Title.Contains("dictation", StringComparison.OrdinalIgnoreCase) == true)
            MetricsText.Text = _dictation.Status;
        else if (_selected is not null)
            MetricsText.Text = $"Executed {_selected.Title} · prototype data only";
    }

    private void ToggleActions()
    {
        if (_historyOpen || _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen) return;
        ActionPanel.Visibility = ActionPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowWaveformOverlay(DisplayArea? targetArea = null)
    {
        try
        {
            if (_overlay is null)
            {
                _overlay = new OverlayWindow(_transcriptPreviewEnabled);
                _overlay.Closed += (_, _) => _overlay = null;
            }
            var area = targetArea ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            _overlay.SetLayout(OverlayPreferences);
            _overlay.SetMode(_overlayMode, area);
            _overlay.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
            _overlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
            _overlay.ActivateWithoutTakingFocus();
            OverlayPreviewPanel.Visibility = _historyOpen || _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen ? Visibility.Collapsed : Visibility.Visible;
            UpdateOverlayControls();
            MetricsText.Text = _overlayMode == PrototypeOverlayMode.Minimal
                ? "Overlay active · minimal indicator"
                : _transcriptPreviewEnabled
                ? "Overlay active · live transcript preview on"
                : "Overlay active · live transcript preview off";
        }
        catch (Exception exception)
        {
            _overlay = null;
            MetricsDot.Fill = new SolidColorBrush(Colors.OrangeRed);
            MetricsText.Text = $"Overlay error · {exception.GetType().Name}: {exception.Message}";
        }
    }

    private void WindowRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (LexiconOpen)
        {
            if (e.Key == global::Windows.System.VirtualKey.Back && FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is not TextBox)
            { _lexicon?.GoBack(); e.Handled = true; }
            return;
        }
        if (FileTranscriptionOpen)
        {
            if (e.Key == global::Windows.System.VirtualKey.Back && FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is not TextBox)
            {
                _fileTranscription?.GoBack(); e.Handled = true;
            }
            return;
        }
        if ((!_historyOpen && !_recorderOpen && !_workflowsOpen && !_pluginsOpen && !_marketplaceOpen) || e.Key != global::Windows.System.VirtualKey.Back) return;
        // Inspect before the editor processes deletion: deleting the last character
        // must not also navigate away from the current page.
        var focused = FocusManager.GetFocusedElement(WindowRoot.XamlRoot);
        if (_workflowsOpen && WorkflowsView.IsConfiguring && focused is TextBox) return;
        if (focused is TextBox { Text.Length: > 0 } or TextBox { AcceptsReturn: true } or PasswordBox or RichEditBox) return;
        foreach (var modifier in new[] { global::Windows.System.VirtualKey.Control, global::Windows.System.VirtualKey.Menu,
                     global::Windows.System.VirtualKey.Shift, global::Windows.System.VirtualKey.LeftWindows, global::Windows.System.VirtualKey.RightWindows })
        {
            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        }
        if (_recorderOpen) RecorderView.GoBack();
        else if (_marketplaceOpen) MarketplaceView.GoBack();
        else if (_pluginsOpen) PluginsView.GoBack();
        else if (_workflowsOpen) WorkflowsView.GoBack();
        else HistoryView.GoBack();
        e.Handled = true;
    }

    private void WindowRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (LexiconOpen)
        {
            if (e.Key == global::Windows.System.VirtualKey.Escape) { _lexicon?.GoBack(); e.Handled = true; }
            return;
        }
        if (FileTranscriptionOpen)
        {
            if (e.Key == global::Windows.System.VirtualKey.Escape) { _fileTranscription?.GoBack(); e.Handled = true; }
            return;
        }
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == (global::Windows.System.VirtualKey)0xBC) // VK_OEM_COMMA
        {
            OpenSettings();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == global::Windows.System.VirtualKey.K)
        {
            ToggleActions();
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            if (_recorderOpen)
                RecorderView.GoBack();
            else if (_marketplaceOpen && MarketplaceView.IsDetail)
                MarketplaceView.GoBack();
            else if (_pluginsOpen && PluginsView.IsDetail)
                PluginsView.GoBack();
            else if (_workflowsOpen && WorkflowsView.IsDetail)
                WorkflowsView.GoBack();
            else if (_historyOpen && HistoryView.IsReading)
                HistoryView.GoBack();
            else if (ActionPanel.Visibility == Visibility.Visible)
                ActionPanel.Visibility = Visibility.Collapsed;
            else if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                _isSearchEditing = false;
                SearchBox.Text = string.Empty;
            }
            else if (_historyOpen)
                CloseHistory();
            else if (_workflowsOpen)
                CloseWorkflows();
            else if (_pluginsOpen)
                ClosePlugins();
            else if (_marketplaceOpen)
                CloseMarketplace();
            else
                AppWindow.Hide();
            e.Handled = true;
            return;
        }

        if (_historyOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            HistoryView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_workflowsOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            WorkflowsView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_pluginsOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            PluginsView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_marketplaceOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            MarketplaceView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (!_historyOpen && !_recorderOpen && !_workflowsOpen && !_pluginsOpen && !_marketplaceOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            if (FilteredItems.Count > 0)
            {
                var offset = e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1;
                CompactResults.SelectedIndex = Math.Clamp(CompactResults.SelectedIndex + offset, 0, FilteredItems.Count - 1);
                CompactResults.ScrollIntoView(CompactResults.SelectedItem);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Enter && FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is not Button)
        {
            if (FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is TextBox { AcceptsReturn: true }) return;
            RunSelected();
            e.Handled = true;
        }
    }

    private PrototypeOverlayPreferences _layoutPreferences = new(PrototypeOverlayMode.Standard, true, false);
    private readonly Dictionary<string, string> _settingsValues = new();
    private PrototypeOverlayPreferences OverlayPreferences => _layoutPreferences with { Mode = _overlayMode, LiveText = _transcriptPreviewEnabled, TechnicalDetails = _technicalDetailsEnabled };

    internal void OpenSetup()
    {
        OpenSettings();
        _settingsWindow?.ShowSetup();
    }

    internal void OpenLexicon(bool snippets = false)
    {
        if (_lexicon is null)
        {
            _lexicon = new PrototypeLexiconView();
            _lexicon.ExitRequested += () =>
            {
                LexiconHost.Visibility = Visibility.Collapsed;
                SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
                OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
                SearchBox.Focus(FocusState.Programmatic);
            };
            LexiconHost.Child = _lexicon;
        }
        SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        LexiconHost.Visibility = Visibility.Visible;
        _lexicon.Present(snippets);
    }

    internal void OpenFileTranscription()
    {
        if (_fileTranscription is null)
        {
            _fileTranscription = new PrototypeFileTranscriptionView();
            _fileTranscription.ExitRequested += () =>
            {
                FileTranscriptionHost.Visibility = Visibility.Collapsed;
                SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
                OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
                SearchBox.Focus(FocusState.Programmatic);
            };
            FileTranscriptionHost.Child = _fileTranscription;
        }
        SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        FileTranscriptionHost.Visibility = Visibility.Visible;
        _fileTranscription.Present();
    }

    internal void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new PrototypeSettingsWindow(OverlayPreferences, _settingsValues);
            _settingsWindow.CommitLauncherHotkeys = ChangeLauncherHotkeys;
            _settingsWindow.CommitDictationHotkeys = ChangeDictationHotkeys;
            _settingsWindow.ConfigureLiveSettings = new LiveDictationSettings(_dictation).Configure;
            _settingsWindow.HistoryRequested += () =>
            {
                if (_historyOpen) { _settingsWindow?.AppWindow.Hide(); ShowFromActivation(); return; }
                if (_recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen)
                {
                    _settingsWindow?.ShowHistoryNavigationHint();
                    return;
                }
                _settingsWindow?.AppWindow.Hide(); ShowFromActivation(); OpenHistory();
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.PreferencesChanged += preferences =>
            {
                var modeChanged = _overlayMode != preferences.Mode || _layoutPreferences.Anchor != preferences.Anchor
                    || _layoutPreferences.Left != preferences.Left || _layoutPreferences.Right != preferences.Right;
                _layoutPreferences = preferences;
                _overlayMode = preferences.Mode;
                _transcriptPreviewEnabled = preferences.LiveText;
                _technicalDetailsEnabled = preferences.TechnicalDetails;
                if (_overlay?.IsPreviewVisible == true)
                {
                    // Keep the existing overlay's monitor and bottom anchor.
                    // A text-only toggle must not resize or move its lower block.
                    _overlay.SetLayout(preferences);
                    if (modeChanged)
                        _overlay.SetMode(_overlayMode, DisplayArea.GetFromWindowId(_overlay.AppWindow.Id, DisplayAreaFallback.Primary));
                    _overlay.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
                    _overlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
                }
                UpdateOverlayControls();
            };
            _settingsWindow.PreviewRequested += (_, _) =>
            {
                if (_overlay?.IsPreviewVisible == true) EndPreview_Click(this, new RoutedEventArgs());
                else ShowWaveformOverlay(DisplayArea.GetFromWindowId(_settingsWindow!.AppWindow.Id, DisplayAreaFallback.Primary));
            };
        }
        _settingsWindow.SetPreferences(OverlayPreferences);
        _settingsWindow.SetPreviewVisible(_overlay?.IsPreviewVisible == true);
        _settingsWindow.ShowOn(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
    }

    internal void OpenDashboard(bool statistics = false)
    {
        OpenSettings(); _settingsWindow!.ShowActivity(statistics);
    }

    internal void OpenSyncBackup()
    {
        OpenSettings(); _settingsWindow!.ShowSyncBackup();
    }

    internal void OpenAccount()
    {
        OpenSettings(); _settingsWindow!.ShowAccount();
    }

    internal void OpenSelectComparison()
    {
        OpenSettings();
        _settingsWindow!.ShowSelectComparison();
    }

    private void OpenMarketplace()
    {
        _launcherQuery = SearchBox.Text;
        _marketplaceOpen = true;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        MarketplaceView.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "Search plugins by name or purpose…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Marketplace search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        MarketplaceView.Filter(string.Empty);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseMarketplace()
    {
        MarketplaceView.ResetNavigation();
        _marketplaceOpen = false;
        MarketplaceView.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = SearchSurface.IsHitTestVisible = true;
        SearchSurface.Opacity = 1;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var command = FilteredItems.FirstOrDefault(item => item.Title == "Marketplace");
        if (command is not null) { CompactResults.SelectedItem = command; UpdateDetail(command); }
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenPlugins()
    {
        _launcherQuery = SearchBox.Text;
        _pluginsOpen = true;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        PluginsView.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "Search plugins by name or category…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Plugin search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        PluginsView.Filter(string.Empty);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void ClosePlugins()
    {
        _pluginsOpen = false;
        PluginsView.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = SearchSurface.IsHitTestVisible = true;
        SearchSurface.Opacity = 1;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var command = FilteredItems.FirstOrDefault(item => item.Title == "Plugins");
        if (command is not null) { CompactResults.SelectedItem = command; UpdateDetail(command); }
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenWorkflows()
    {
        _launcherQuery = SearchBox.Text;
        _workflowsOpen = true;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        WorkflowsView.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "Search workflows by name or purpose…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Workflow search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        WorkflowsView.Filter(string.Empty);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseWorkflows()
    {
        _workflowsOpen = false;
        WorkflowsView.Visibility = Visibility.Collapsed;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var command = FilteredItems.FirstOrDefault(item => item.Title == "Workflows");
        if (command is not null) { CompactResults.SelectedItem = command; UpdateDetail(command); }
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    internal void ShowHistoryFromTray()
    {
        ShowFromActivation();
        if (_historyOpen) return;
        if (_recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen)
        {
            MetricsText.Text = "Return to Quick Launch before opening History.";
            return;
        }
        OpenHistory();
    }

    private void OpenHistory()
    {
        _ = HistoryView.RefreshAsync();
        _launcherQuery = SearchBox.Text;
        _historyOpen = true;
        CommandSurface.Visibility = Visibility.Collapsed;
        QuickLaunchFooter.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        SearchPlaceholder.Text = "Search history by title or transcript…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "History search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        HistoryView.Filter(string.Empty);
        NavigationHint.Text = "↑↓ Navigate   Enter Open   ⌫ / Esc Back";
        MetricsText.Text = "History · local data";
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseHistory()
    {
        _historyOpen = false;
        HistoryView.Visibility = Visibility.Collapsed;
        CommandSurface.Visibility = Visibility.Visible;
        QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var historyCommand = FilteredItems.FirstOrDefault(command => command.Title == "History");
        if (historyCommand is not null)
        {
            CompactResults.SelectedItem = historyCommand;
            UpdateDetail(historyCommand);
        }
        NavigationHint.Text = "↑↓ Navigate   Enter Run   Ctrl K Actions   Esc Hide";
        MetricsText.Text = "Quick Launch";
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenRecorder()
    {
        _launcherQuery = SearchBox.Text;
        _recorderOpen = true;
        CommandSurface.Visibility = Visibility.Collapsed;
        QuickLaunchFooter.Visibility = Visibility.Collapsed;
        OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        RecorderView.Visibility = Visibility.Visible;
        RecorderView.SetPresented(true);
        SearchPlaceholder.Text = "Name this recording (optional)…";
        SearchGlyph.Kind = "file";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Recording title");
        SearchBox.Text = RecorderView.SessionTitle;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        RecorderView.FocusEntry();
    }

    private void CloseRecorder()
    {
        _recorderOpen = false;
        RecorderView.SetPresented(false);
        RecorderView.Visibility = Visibility.Collapsed;
        CommandSurface.Visibility = Visibility.Visible;
        QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        SearchGlyph.Kind = "search";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var recorder = FilteredItems.FirstOrDefault(command => command.Title == "Recorder");
        if (recorder is not null) CompactResults.SelectedItem = recorder;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        ((OverlappedPresenter)AppWindow.Presenter).Minimize();
    private void HideButton_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();
    private void TestWaveformButton_Click(object sender, RoutedEventArgs e) => ShowWaveformOverlay();
    private void OverlayMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode } && Enum.TryParse<PrototypeOverlayMode>(mode, out var selected))
        {
            _overlayMode = selected;
            ShowWaveformOverlay();
        }
    }

    private void PausePreview_Click(object sender, RoutedEventArgs e)
    {
        _overlay?.TogglePaused();
        UpdateOverlayControls();
    }

    private void EndPreview_Click(object sender, RoutedEventArgs e)
    {
        _overlay?.HidePreview();
        OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        MetricsText.Text = "Overlay preview ended";
        UpdateOverlayControls();
    }

    private void UpdateOverlayControls()
    {
        _settingsWindow?.SetPreferences(OverlayPreferences);
        _settingsWindow?.SetPreviewVisible(_overlay?.IsPreviewVisible == true);
        foreach (var button in new[] { StandardOverlayButton, CompactOverlayButton, MinimalOverlayButton })
        {
            var selected = (string)button.Tag == _overlayMode.ToString();
            button.Style = (Style)Application.Current.Resources[selected
                ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"];
        }
        PausePreviewButton.Content = _overlay?.IsPaused == true ? "Resume" : "Pause";
        var minimal = _overlayMode == PrototypeOverlayMode.Minimal && _overlay?.IsPreviewVisible == true;
        TranscriptToggleButton.IsEnabled = !minimal;
        TranscriptToggleButton.Content = minimal ? "Live text  —" : _transcriptPreviewEnabled ? "Live text  On" : "Live text  Off";
        OverlayPreviewHint.Text = minimal
            ? "Minimal: indicator only at the screen edge · live-text preference remembered"
            : "Microphone level only · transcript is sample text · no audio saved";
    }
    private void TranscriptToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _transcriptPreviewEnabled = !_transcriptPreviewEnabled;
        TranscriptToggleButton.Content = _transcriptPreviewEnabled ? "Live text  On" : "Live text  Off";
        TranscriptToggleButton.Foreground = (Brush)Application.Current.Resources[
            _transcriptPreviewEnabled ? "AccentBrush" : "MutedBrush"];
        _overlay?.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
        UpdateOverlayControls();
        MetricsText.Text = _transcriptPreviewEnabled
            ? "Live transcript preview enabled"
            : "Live transcript preview disabled";
    }
    private void RunSelectedButton_Click(object sender, RoutedEventArgs e) => RunSelected();
    private void ShowActionsButton_Click(object sender, RoutedEventArgs e) => ToggleActions();
    private void KeepOpenButton_Click(object sender, RoutedEventArgs e)
    {
        RunSelected();
        SearchBox.Focus(FocusState.Programmatic);
    }
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        ActionPanel.Visibility = Visibility.Collapsed;
        MetricsText.Text = _selected is null ? "Nothing selected" : $"Pinned {_selected.Title} · in-memory prototype";
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }
}
