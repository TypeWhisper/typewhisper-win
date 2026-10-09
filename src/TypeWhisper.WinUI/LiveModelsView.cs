using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

internal sealed class LiveModelsView : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly bool _setup;
    private readonly StackPanel _panel = new() { Spacing = 12 };
    private readonly StackPanel _cards = new();
    private readonly TextBlock _vocabulary = Copy("", 12, true);
    private readonly HandCursorButton _setupAction = Button(Loc.T("Retry setup"), Loc.T("Retry dictionary boosting setup"));
    private readonly TextBlock _feedback = Copy("", 12, true);
    private readonly SettingsCard _device = new();
    private readonly SettingsRow _deviceRow = new();
    private readonly ChoicePicker _devicePicker = new();
    private readonly List<ModelRow> _rows = [];
    private string? _message;
    private bool _confirmingRemoval;
    private sealed record ModelRow(PluginModelInfo Model, Border Badge, TextBlock Status, HandCursorButton Action, HandCursorButton Remove, HandCursorButton Cancel, Border Progress, Border Fill);

    internal LiveModelsView(LocalDictationSession session, bool setup = false)
    {
        _session = session; _setup = setup;
        Tag = "SelectedModelId";
        var list = new StackPanel();
        if (!setup)
        {
            var heading = Copy(Loc.T("Models"), 13);
            heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            list.Children.Add(heading);
            var intro = Copy(Loc.T("Local models support dictation and live preview. Downloads continue when you leave this page."), 12, true);
            intro.Margin = new Thickness(0, 4, 0, 14);
            list.Children.Add(intro);
        }
        list.Children.Add(_cards);
        _panel.Children.Add(new Border { Child = list, Padding = new Thickness(18, setup ? 2 : 16, 18, 2), CornerRadius = new CornerRadius(12),
            Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) });
        if (!setup)
        {
            _devicePicker.Configure(Loc.T("Processing device"), "chip", Loc.T("Processing device"));
            _devicePicker.SelectionChanged += async id =>
            {
                _message = null;
                _message = await _session.SetLocalAccelerationAsync(Enum.Parse<TranscriptionAccelerationPreference>(id));
                if (IsLoaded) Update();
            };
            _deviceRow.Set(Loc.T("Processing device"), "", Loc.T("Automatic uses NVIDIA CUDA once it is installed and the CPU otherwise. The first switch to NVIDIA CUDA downloads about 2 GB of NVIDIA libraries and needs an NVIDIA graphics card. If the graphics card fails, TypeWhisper keeps the previous device."), _devicePicker);
            _device.Children.Add(_deviceRow);
            _panel.Children.Add(_device);
            _panel.Children.Add(_vocabulary);
        }
        _setupAction.HorizontalAlignment = HorizontalAlignment.Left;
        _setupAction.Click += async (_, _) =>
        {
            if (_session.CtcVocabulary.Busy) _session.CtcVocabulary.RequestCancelActivation();
            else _message = await _session.SetLocalPluginEnabledAsync(true);
            if (IsLoaded) Update();
        };
        _panel.Children.Add(_setupAction);
        AutomationProperties.SetLiveSetting(_feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _panel.Children.Add(_feedback);
        Content = _panel;
        ViewSubscriptions.Attach(this, () => { _session.Models.Changed += Refresh; _session.CtcVocabulary.Changed += Refresh; _session.Changed += Refresh; Update(); },
            () => { _session.Models.Changed -= Refresh; _session.CtcVocabulary.Changed -= Refresh; _session.Changed -= Refresh; });
        Update();
    }

    private void Refresh() => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) Update(); });
    private void Update()
    {
        var models = _session.Models;
        var states = models.Models;
        if (!_rows.Select(r => r.Model.Id).SequenceEqual(states.Select(s => s.Model.Id)))
        {
            _cards.Children.Clear(); _rows.Clear();
            foreach (var item in states) _cards.Children.Add(CreateRow(item.Model));
        }
        _vocabulary.Text = _session.CtcVocabulary.Error ?? (_session.CtcVocabulary.Busy
            ? _session.CtcVocabulary.Status ?? Loc.T("Preparing dictionary boosting…") : _session.CtcVocabulary.Enabled
                ? Loc.T("Dictionary boosting is included for Parakeet. Add terms in Dictionary.")
                : Loc.T("Dictionary boosting follows this plugin’s enablement."));
        _setupAction.Visibility = models.Enabled && (_session.CtcVocabulary.Busy || !_session.CtcVocabulary.Enabled)
            ? Visibility.Visible : Visibility.Collapsed;
        _setupAction.Content = _session.CtcVocabulary.Busy ? Loc.T("Cancel setup") : Loc.T("Retry setup");
        AutomationProperties.SetName(_setupAction, _session.CtcVocabulary.Busy ? Loc.T("Cancel dictionary boosting setup") : Loc.T("Retry dictionary boosting setup"));
        _setupAction.IsEnabled = _session.CtcVocabulary.Busy || (_session.CanChangeProvider && !models.Busy);
        _feedback.Text = _message ?? models.Error ?? models.Feedback
            ?? Loc.T("Choose a downloaded model to use it. Downloads do not change your active model.");
        if (!_setup) UpdateDevice(models);
        foreach (var row in _rows)
        {
            var state = states.Single(s => s.Model.Id == row.Model.Id);
            var downloading = models.DownloadingModelId == row.Model.Id;
            var removing = models.RemovingModelId == row.Model.Id;
            var active = !_session.UsesRegistryProvider && models.ActiveModelId == row.Model.Id;
            row.Status.Text = removing ? Loc.T("Removing downloaded files…") : downloading ? Loc.T("Downloading · {0:P0}", models.Progress) : active ? Loc.T("Active · ready for dictation") : state.Downloaded ? Loc.T("Downloaded · ready to activate") : Loc.T("Available to download");
            row.Status.Visibility = downloading || removing ? Visibility.Visible : Visibility.Collapsed;
            row.Action.Content = downloading ? $"{models.Progress:P0}" : active ? Loc.T("Active") : state.Downloaded ? Loc.T("Use model") : Loc.T("Download");
            row.Action.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            row.Badge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            row.Action.IsEnabled = !_confirmingRemoval && !models.Busy && !active && (!state.Downloaded || _session.CanSelectModel);
            row.Remove.Visibility = !_setup && models.SupportsModelRemoval && state.Downloaded ? Visibility.Visible : Visibility.Collapsed;
            row.Remove.IsEnabled = !_confirmingRemoval && !models.Busy && _session.CanChangeProvider && models.CanRemoveModel(row.Model.Id);
            ToolTipService.SetToolTip(row.Remove, models.CanRemoveModel(row.Model.Id)
                ? Loc.T("Remove downloaded files for this model.") : Loc.T("Select a different model in this plugin before removing this one."));
            row.Cancel.Visibility = downloading || removing ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(row.Cancel, removing ? Loc.T("Cancel {0} removal", row.Model.DisplayName) : Loc.T("Cancel {0} download", row.Model.DisplayName));
            row.Progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            row.Fill.Width = row.Progress.ActualWidth * models.Progress;
            AutomationProperties.SetName(row.Action, $"{row.Action.Content} {row.Model.DisplayName}");
            AutomationProperties.SetItemStatus(row.Action, row.Status.Text);
        }
    }

    private void UpdateDevice(LocalTranscriptionPlugin models)
    {
        _device.Visibility = models.Enabled && models.SupportsCuda ? Visibility.Visible : Visibility.Collapsed;
        var selected = models.Acceleration.ToString();
        if (_devicePicker.SelectedId != selected)
            _devicePicker.SetOptions([
                new(nameof(TranscriptionAccelerationPreference.Auto), Loc.T("Automatic"), Loc.T("NVIDIA CUDA once it is installed, otherwise the CPU")),
                new(nameof(TranscriptionAccelerationPreference.Cpu), "CPU", Loc.T("Works on every PC")),
                new(nameof(TranscriptionAccelerationPreference.NvidiaCuda), "NVIDIA CUDA", Loc.T("Needs an NVIDIA graphics card. Downloads about 2 GB the first time."))], selected);
        _devicePicker.IsEnabled = !_confirmingRemoval && !models.Busy && _session.CanSelectModel;
        _deviceRow.Description = models.ActiveBackend switch
        {
            TranscriptionAccelerationBackend.NvidiaCuda => Loc.T("In use: {0}", "NVIDIA CUDA"),
            TranscriptionAccelerationBackend.Cpu => Loc.T("In use: {0}", "CPU"),
            _ => ""
        };
    }

    private Border CreateRow(PluginModelInfo model)
    {
        var layout = new Grid { ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var name = Copy(model.DisplayName, _setup ? 14 : 15);
        name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var badgeText = Copy(Loc.T("Active"), 11);
        badgeText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; badgeText.Foreground = Brush("AccentBrush");
        var badge = new Border { Child = badgeText, Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(6),
            Background = Brush("ElevatedBrush"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        // Left-aligned, the star column is as wide as the name but still wraps a long one.
        var title = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        title.Children.Add(name); Grid.SetColumn(badge, 1); title.Children.Add(badge);
        copy.Children.Add(title);
        var languages = Button(Loc.T("{0} languages", model.LanguageCount), Loc.T("Languages supported by {0}", model.DisplayName));
        languages.Padding = new Thickness(0); languages.BorderThickness = new Thickness(0); languages.MinHeight = 0;
        languages.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        languages.VerticalAlignment = VerticalAlignment.Center; languages.FontSize = 12;
        languages.FontWeight = Microsoft.UI.Text.FontWeights.Normal; languages.Foreground = Brush("MutedBrush");
        var facts = string.Join(" · ", new[] { model.SizeDescription, model.Publisher }.Where(fact => !string.IsNullOrWhiteSpace(fact)));
        var meta = new StackPanel { Orientation = Orientation.Horizontal };
        // A TextBlock drops trailing spaces, so the gap after the separator is a margin.
        if (facts.Length > 0) { meta.Children.Add(Copy(facts + " ·", 12, true)); languages.Margin = new Thickness(4, 0, 0, 0); }
        meta.Children.Add(languages);
        var languageNames = model.LanguageCodes.Select(code =>
        {
            try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
            catch (System.Globalization.CultureNotFoundException) { return code; }
        }).Order(StringComparer.CurrentCulture).ToArray();
        var description = languageNames.Length == 0 ? Loc.T("The plugin has not supplied a language list.") : string.Join(", ", languageNames);
        AutomationProperties.SetHelpText(languages, description);
        var tooltip = new ToolTip { Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } };
        ToolTipService.SetToolTip(languages, tooltip);
        // Focus also lands here when a download disables the Download button; only keyboard focus opens the list.
        languages.GotFocus += (_, _) => tooltip.IsOpen = languages.FocusState == FocusState.Keyboard;
        languages.LostFocus += (_, _) => tooltip.IsOpen = false;
        languages.Click += (_, _) => tooltip.IsOpen = true;
        languages.PointerExited += (_, e) =>
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && languages.FocusState != FocusState.Keyboard)
                tooltip.IsOpen = false;
        };
        languages.KeyDown += (_, e) =>
        {
            if (e.Key == global::Windows.System.VirtualKey.Escape && tooltip.IsOpen)
            { tooltip.IsOpen = false; e.Handled = true; }
        };
        languages.Unloaded += (_, _) => tooltip.IsOpen = false;
        copy.Children.Add(meta);
        var status = Copy("", 12, true); copy.Children.Add(status);
        var fill = new Border { Background = Brush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(2) };
        var progress = new Border { Background = Brush("HairlineBrush"), Child = fill, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 0, 0) };
        progress.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * _session.Models.Progress;
        AutomationProperties.SetName(progress, Loc.T("{0} download progress", model.DisplayName));
        copy.Children.Add(progress); layout.Children.Add(copy);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var action = Button(Loc.T("Download"), Loc.T("Download {0}", model.DisplayName));
        action.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
        action.MinWidth = 104;
        action.Click += async (_, _) =>
        {
            _message = null;
            try
            {
                if (_session.Models.Models.Single(m => m.Model.Id == model.Id).Downloaded)
                    _message = await _session.SelectModelAsync(model.Id);
                else await _session.Models.DownloadAsync(model.Id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _message = ex.Message; }
            if (IsLoaded) { Update(); (action.Visibility == Visibility.Visible ? action : languages).Focus(FocusState.Programmatic); }
        };
        var cancel = Button(Loc.T("Cancel"), Loc.T("Cancel {0} download", model.DisplayName));
        cancel.Click += async (_, _) =>
        {
            if (_session.Models.RemovingModelId == model.Id)
            {
                try { await _session.CancelRegistryModelDownloadAsync(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _message = Loc.T("The model operation could not finish stopping. Wait before retrying."); }
                if (IsLoaded) Update();
            }
            else _session.Models.CancelDownload();
        };
        var remove = Button(Loc.T("Remove"), Loc.T("Remove {0}", model.DisplayName));
        remove.Click += async (_, _) =>
        {
            if (_confirmingRemoval || !remove.IsEnabled) return;
            var expectedGeneration = _session.Models.Generation;
            _confirmingRemoval = true; Update();
            try
            {
                if (!await Dialogs.ConfirmAsync(this, Loc.T("Remove {0}?", model.DisplayName),
                    Loc.T("Downloaded files for this model will be removed. You will need to download it again before using it. The plugin and its settings will be kept."), Loc.T("Remove model"), destructive: true) || !IsLoaded) return;
                _message = await _session.RemoveLocalModelAsync(model.Id, expectedGeneration);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _message = ex.Message; }
            finally { _confirmingRemoval = false; if (IsLoaded) Update(); }
        };
        actions.Children.Add(action); actions.Children.Add(cancel); actions.Children.Add(remove);
        Grid.SetColumn(actions, 1); layout.Children.Add(actions);
        // Rows share one card; a hairline separates each from the heading or the row above.
        var card = new Border { Child = layout, Padding = new Thickness(0, 14, 0, 14), BorderBrush = Brush("HairlineBrush"),
            BorderThickness = new Thickness(0, _setup && _rows.Count == 0 ? 0 : 1, 0, 0) };
        card.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 440;
            Grid.SetColumnSpan(copy, narrow ? 2 : 1);
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
            actions.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        };
        _rows.Add(new(model, badge, status, action, remove, cancel, progress, fill));
        return card;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new() { Text = text, FontSize = size,
        TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
    private static HandCursorButton Button(string text, string name)
    {
        var button = new HandCursorButton { Content = text, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        AutomationProperties.SetName(button, name); return button;
    }
}
