using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

internal sealed class LiveModelsView : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly StackPanel _panel = new() { Spacing = 12 };
    private readonly StackPanel _cards = new() { Spacing = 12 };
    private readonly TextBlock _active = Copy("", 16);
    private readonly TextBlock _vocabulary = Copy("", 12, true);
    private readonly TextBlock _feedback = Copy("", 12, true);
    private readonly List<ModelRow> _rows = [];
    private string? _message;
    private sealed record ModelRow(PluginModelInfo Model, Border Card, TextBlock Status, HandCursorButton Action, HandCursorButton Cancel, Border Progress, Border Fill);

    internal LiveModelsView(LocalDictationSession session)
    {
        _session = session;
        Tag = "SelectedModelId";
        _panel.Children.Add(Copy("ACTIVE MODEL", 10, true));
        _panel.Children.Add(_active);
        _panel.Children.Add(Copy("Local models support dictation and live preview. Downloads continue when you leave this page.", 12, true));
        _panel.Children.Add(_cards);
        _panel.Children.Add(_vocabulary);
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
            ? "Preparing dictionary boosting…" : _session.CtcVocabulary.Enabled
                ? "Dictionary boosting is included for Parakeet. Add terms in Dictionary."
                : "Dictionary boosting follows this plugin’s enablement.");
        _feedback.Text = _message ?? models.Error ?? models.Feedback
            ?? "Choose a downloaded model to use it. Downloads do not change your active model.";
        foreach (var row in _rows)
        {
            var state = states.Single(s => s.Model.Id == row.Model.Id);
            var downloading = models.DownloadingModelId == row.Model.Id;
            var active = !_session.UsesGroq && models.ActiveModelId == row.Model.Id;
            row.Status.Text = downloading ? $"Downloading · {models.Progress:P0}" : active ? "Active · ready for dictation" : state.Downloaded ? "Downloaded · ready to activate" : "Available to download";
            row.Action.Content = downloading ? $"{models.Progress:P0}" : active ? "Active" : state.Downloaded ? "Use model" : "Download";
            row.Action.IsEnabled = !models.Busy && !active && (!state.Downloaded || _session.CanSelectModel);
            row.Cancel.Visibility = row.Progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
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
        copy.Children.Add(Copy("LOCAL MODELS · ON-DEVICE · " + model.Publisher + " · " + model.SizeDescription, 10, true));
        copy.Children.Add(Copy(model.DisplayName, 16));
        var languages = Button($"{model.LanguageCount} languages", $"Languages supported by {model.DisplayName}");
        languages.Padding = new Thickness(0); languages.BorderThickness = new Thickness(0);
        languages.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        languages.HorizontalAlignment = HorizontalAlignment.Left; languages.FontSize = 12;
        var languageNames = model.LanguageCodes.Select(code =>
        {
            try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
            catch (System.Globalization.CultureNotFoundException) { return code; }
        }).Order(StringComparer.CurrentCulture).ToArray();
        var description = languageNames.Length == 0 ? "The plugin has not supplied a language list." : string.Join(", ", languageNames);
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
        AutomationProperties.SetName(progress, model.DisplayName + " download progress");
        copy.Children.Add(progress); layout.Children.Add(copy);
        var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var action = Button("Download", "Download " + model.DisplayName);
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
        var cancel = Button("Cancel", "Cancel " + model.DisplayName + " download");
        cancel.Click += (_, _) => _session.Models.CancelDownload();
        actions.Children.Add(action); actions.Children.Add(cancel);
        Grid.SetColumn(actions, 1); layout.Children.Add(actions);
        var card = new Border { Child = layout, Padding = new Thickness(16), CornerRadius = new CornerRadius(10),
            Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) };
        card.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 480;
            Grid.SetColumnSpan(copy, narrow ? 2 : 1);
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
            actions.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        };
        _rows.Add(new(model, card, status, action, cancel, progress, fill));
        return card;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new() { Text = text, FontSize = size,
        TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
    private static HandCursorButton Button(string text, string name)
    {
        var button = new HandCursorButton { Content = text, Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        AutomationProperties.SetName(button, name); return button;
    }
}
