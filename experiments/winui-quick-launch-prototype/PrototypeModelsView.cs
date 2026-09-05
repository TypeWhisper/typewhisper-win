using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUIPrototype;

public sealed class PrototypeModelsView : UserControl
{
    private readonly PrototypeModelSession _session;
    private readonly DispatcherQueueTimer _timer;
    private readonly TextBlock _active = Copy("", 16);
    private readonly TextBlock _feedback = Copy("All downloads are simulated. Nothing is downloaded or loaded into memory.", 12, true);
    private readonly List<ModelRow> _rows = [];
    private sealed record ModelRow(PrototypeModelOption Model, Border Card, TextBlock Status,
        HandCursorButton Action, HandCursorButton Cancel, Border Progress, Border Fill);

    internal PrototypeModelsView(Dictionary<string, string> values)
    {
        _session = new(values);
        Tag = "SelectedModelId";
        var panel = new StackPanel { Spacing = 12 };
        var summary = new StackPanel { Spacing = 5 };
        summary.Children.Add(Copy("ACTIVE MODEL", 10, true));
        summary.Children.Add(_active);
        summary.Children.Add(Copy("Your default for dictation and recordings without an override.", 12, true));
        panel.Children.Add(new Border
        {
            Child = summary, Padding = new Thickness(16), CornerRadius = new CornerRadius(10),
            Background = Brush("ElevatedBrush"), Margin = new Thickness(0, 0, 0, 8)
        });
        foreach (var model in PrototypeModelSession.Models) panel.Children.Add(CreateRow(model));
        AutomationProperties.SetLiveSetting(_feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(_feedback);
        Content = panel;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(350);
        _timer.Tick += (_, _) =>
        {
            if (_session.Advance())
            {
                _timer.Stop();
                _feedback.Text = "Demo download complete. Choose Use model to make it your default.";
            }
            Update();
        };
        Unloaded += (_, _) => { _timer.Stop(); _session.CancelDownload(); };
        Update();
    }

    private FrameworkElement CreateRow(PrototypeModelOption model)
    {
        var layout = new Grid { ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TypeWhisperGlyph { Kind = "chip", Width = 24, Height = 24 });
        var copy = new StackPanel { Spacing = 5 };
        copy.Children.Add(Copy(model.Badge + " · DEMO", 10, true));
        var title = Copy(model.Title, 16); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        copy.Children.Add(title); copy.Children.Add(Copy(model.Description, 12, true));
        var status = Copy("", 12, true); copy.Children.Add(status);
        Grid.SetColumn(copy, 1); layout.Children.Add(copy);
        var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var action = Button("", $"Choose {model.Title} demo model");
        action.MinWidth = 124;
        action.Click += (_, _) =>
        {
            if (_session.IsDownloaded(model.Id))
            {
                if (_session.Activate(model.Id)) _feedback.Text = $"{model.Title} is now the default for this preview session. No real engine is loaded.";
            }
            else if (_session.StartDownload(model.Id))
            {
                _feedback.Text = "Simulating a download. You can cancel; leaving this page also cancels the simulation.";
                _timer.Start();
            }
            Update();
        };
        var cancel = Button("Cancel", $"Cancel {model.Title} demo download");
        cancel.Click += (_, _) =>
        {
            _timer.Stop(); _session.CancelDownload();
            _feedback.Text = "Demo download canceled. Your active model is unchanged.";
            Update(); action.Focus(FocusState.Keyboard);
        };
        actions.Children.Add(action); actions.Children.Add(cancel);
        Grid.SetColumn(actions, 2); layout.Children.Add(actions);
        var fill = new Border { Background = Brush("AccentBrush"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var progress = new Border
        {
            Background = Brush("HairlineBrush"), CornerRadius = new CornerRadius(2), Child = fill,
            Height = 4, Margin = new Thickness(0, 10, 0, 0)
        };
        progress.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * _session.Progress / 100.0;
        AutomationProperties.SetName(progress, $"{model.Title} simulated download progress");
        copy.Children.Add(progress);
        var card = new Border
        {
            Child = layout, Padding = new Thickness(16), CornerRadius = new CornerRadius(10),
            Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1)
        };
        card.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 560;
            Grid.SetColumnSpan(copy, narrow ? 2 : 1);
            Grid.SetColumn(actions, narrow ? 1 : 2); Grid.SetRow(actions, narrow ? 1 : 0);
            actions.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        _rows.Add(new(model, card, status, action, cancel, progress, fill));
        return card;
    }

    private void Update()
    {
        var active = PrototypeModelSession.Models.FirstOrDefault(model => model.Id == _session.Active);
        _active.Text = active is not null ? active.Title + " · Demo" : _session.Active == "Not selected" ? "Choose your default model" : _session.Active;
        foreach (var row in _rows)
        {
            var downloading = _session.Downloading == row.Model.Id;
            var selected = _session.Active == row.Model.Id;
            var downloaded = _session.IsDownloaded(row.Model.Id);
            row.Status.Text = selected ? "Active · ready in this preview" : downloading ? $"Simulated download · {_session.Progress}%" : downloaded ? "Downloaded · ready to activate" : "Available · demo download";
            row.Action.Content = selected ? "Active" : downloading ? $"{_session.Progress}%" : downloaded ? "Use model" : "Try download";
            row.Action.IsEnabled = !selected && !downloading && (downloaded || _session.Downloading is null);
            row.Action.Style = (Style)Application.Current.Resources[downloaded ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"];
            AutomationProperties.SetItemStatus(row.Action, row.Status.Text);
            row.Cancel.Visibility = row.Progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            row.Fill.Width = row.Progress.ActualWidth * _session.Progress / 100.0;
            row.Card.BorderBrush = Brush(selected ? "AccentBrush" : "HairlineBrush");
        }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(muted ? "MutedBrush" : "TextBrush")
    };
    private static HandCursorButton Button(string text, string name)
    {
        var button = new HandCursorButton { Content = text, Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        AutomationProperties.SetName(button, name); return button;
    }
}
