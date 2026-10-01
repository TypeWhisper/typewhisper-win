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
    private readonly StackPanel _cards = new() { Spacing = 12 };
    private readonly TextBlock _active = Copy("", 16);
    private readonly TextBlock _vocabulary = Copy("", 12, true);
    private readonly HandCursorButton _setupAction = Button(Loc.T("Retry setup"), Loc.T("Retry dictionary boosting setup"));
    private readonly TextBlock _feedback = Copy("", 12, true);
    private readonly List<ModelRow> _rows = [];
    private string? _message;
    private bool _confirmingRemoval;
    private sealed record ModelRow(PluginModelInfo Model, Border Card, TextBlock Status, HandCursorButton Action, HandCursorButton Remove, HandCursorButton Cancel, Border Progress, Border Fill);

    internal LiveModelsView(LocalDictationSession session, bool setup = false)
    {
        _session = session; _setup = setup;
        Tag = "SelectedModelId";
        if (!setup)
        {
            _panel.Children.Add(Copy(Loc.T("ACTIVE MODEL"), 10, true));
            _panel.Children.Add(_active);
            _panel.Children.Add(Copy(Loc.T("Local models support dictation and live preview. Downloads continue when you leave this page."), 12, true));
        }
        _panel.Children.Add(_cards);
        if (!setup) _panel.Children.Add(_vocabulary);
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
        Loaded += (_, _) => { _session.Models.Changed += Refresh; _session.CtcVocabulary.Changed += Refresh; _session.Changed += Refresh; Update(); };
        Unloaded += (_, _) => { _session.Models.Changed -= Refresh; _session.CtcVocabulary.Changed -= Refresh; _session.Changed -= Refresh; };
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
        _active.Text = _session.ActiveModelName;
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
        foreach (var row in _rows)
        {
            var state = states.Single(s => s.Model.Id == row.Model.Id);
            var downloading = models.DownloadingModelId == row.Model.Id;
            var removing = models.RemovingModelId == row.Model.Id;
            var active = !_session.UsesRegistryProvider && models.ActiveModelId == row.Model.Id;
            row.Status.Text = removing ? Loc.T("Removing downloaded files…") : downloading ? Loc.T("Downloading · {0:P0}", models.Progress) : active ? Loc.T("Active · ready for dictation") : state.Downloaded ? Loc.T("Downloaded · ready to activate") : Loc.T("Available to download");
            row.Action.Content = downloading ? $"{models.Progress:P0}" : active ? Loc.T("Active") : state.Downloaded ? Loc.T("Use model") : Loc.T("Download");
            row.Action.IsEnabled = !_confirmingRemoval && !models.Busy && !active && (!state.Downloaded || _session.CanSelectModel);
            row.Remove.Visibility = !_setup && models.SupportsModelRemoval && state.Downloaded ? Visibility.Visible : Visibility.Collapsed;
            row.Remove.IsEnabled = !_confirmingRemoval && !models.Busy && _session.CanChangeProvider && models.CanRemoveModel(row.Model.Id);
            ToolTipService.SetToolTip(row.Remove, models.CanRemoveModel(row.Model.Id)
                ? Loc.T("Remove downloaded files for this model.") : Loc.T("Select a different model in this plugin before removing this one."));
            row.Cancel.Visibility = downloading || removing ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(row.Cancel, removing ? Loc.T("Cancel {0} removal", row.Model.DisplayName) : Loc.T("Cancel {0} download", row.Model.DisplayName));
            row.Progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            row.Fill.Width = row.Progress.ActualWidth * models.Progress;
            row.Card.BorderBrush = Brush(active ? "AccentBrush" : "HairlineBrush");
            AutomationProperties.SetName(row.Action, $"{row.Action.Content} {row.Model.DisplayName}");
            AutomationProperties.SetItemStatus(row.Action, row.Status.Text);
        }
    }

    private Border CreateRow(PluginModelInfo model)
    {
        var layout = new Grid { ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var copy = new StackPanel { Spacing = 5 };
        copy.Children.Add(Copy(Loc.T("LOCAL MODELS · ON-DEVICE · {0} · {1}", model.Publisher, model.SizeDescription), 10, true));
        copy.Children.Add(Copy(model.DisplayName, _setup ? 14 : 16));
        var languages = Button(Loc.T("{0} languages", model.LanguageCount), Loc.T("Languages supported by {0}", model.DisplayName));
        languages.Padding = new Thickness(0); languages.BorderThickness = new Thickness(0);
        languages.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        languages.HorizontalAlignment = HorizontalAlignment.Left; languages.FontSize = 12;
        var languageNames = model.LanguageCodes.Select(code =>
        {
            try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
            catch (System.Globalization.CultureNotFoundException) { return code; }
        }).Order(StringComparer.CurrentCulture).ToArray();
        var description = languageNames.Length == 0 ? Loc.T("The plugin has not supplied a language list.") : string.Join(", ", languageNames);
        AutomationProperties.SetHelpText(languages, description);
        var tooltip = new ToolTip { Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } };
        ToolTipService.SetToolTip(languages, tooltip);
        languages.GotFocus += (_, _) => tooltip.IsOpen = true;
        languages.LostFocus += (_, _) => tooltip.IsOpen = false;
        languages.Click += (_, _) => tooltip.IsOpen = true;
        languages.Unloaded += (_, _) => tooltip.IsOpen = false;
        copy.Children.Add(languages);
        var status = Copy("", 12, true); copy.Children.Add(status);
        var fill = new Border { Background = Brush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(2) };
        var progress = new Border { Background = Brush("HairlineBrush"), Child = fill, Height = 4, CornerRadius = new CornerRadius(2) };
        progress.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * _session.Models.Progress;
        AutomationProperties.SetName(progress, Loc.T("{0} download progress", model.DisplayName));
        copy.Children.Add(progress); layout.Children.Add(copy);
        var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var action = Button(Loc.T("Download"), Loc.T("Download {0}", model.DisplayName));
        action.MinWidth = 124;
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
            if (IsLoaded) { Update(); action.Focus(FocusState.Programmatic); }
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
        var remove = Button(Loc.T("Remove model"), Loc.T("Remove {0}", model.DisplayName));
        remove.Click += async (_, _) =>
        {
            if (_confirmingRemoval || !remove.IsEnabled) return;
            var expectedGeneration = _session.Models.Generation;
            _confirmingRemoval = true; Update();
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                    Title = Loc.T("Remove {0}?", model.DisplayName),
                    Content = Loc.T("Downloaded files for this model will be removed. You will need to download it again before using it. The plugin and its settings will be kept."),
                    PrimaryButtonText = Loc.T("Remove model"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsLoaded) return;
                _message = await _session.RemoveLocalModelAsync(model.Id, expectedGeneration);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _message = ex.Message; }
            finally { _confirmingRemoval = false; if (IsLoaded) Update(); }
        };
        actions.Children.Add(action); actions.Children.Add(remove); actions.Children.Add(cancel);
        Grid.SetColumn(actions, 1); layout.Children.Add(actions);
        var card = new Border { Child = layout, Padding = new Thickness(_setup ? 12 : 16), CornerRadius = new CornerRadius(10),
            Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) };
        card.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 480;
            Grid.SetColumnSpan(copy, narrow ? 2 : 1);
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
            actions.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        };
        _rows.Add(new(model, card, status, action, remove, cancel, progress, fill));
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
