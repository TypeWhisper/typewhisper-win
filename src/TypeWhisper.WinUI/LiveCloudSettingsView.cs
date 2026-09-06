using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

internal sealed class LiveCloudSettingsView : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly PasswordBox _key = new() { Style = (Style)Application.Current.Resources["PrototypeApiKeyPasswordBoxStyle"] };
    private readonly TextBlock _placeholder = Copy("Paste your Groq API key", 13, true);
    private readonly TextBlock _status = Copy("", 11, true);
    private readonly TextBlock _feedback = Copy("", 12, true);
    private readonly HandCursorButton _save = Button("Save key", true);
    private readonly HandCursorButton _remove = Button("Remove key");
    private readonly HandCursorButton _check = Button("Check connection");
    private readonly StackPanel _models = new() { Spacing = 0 };
    private readonly List<(string Id, HandCursorButton Button)> _modelButtons = [];
    private string? _message;

    internal LiveCloudSettingsView(LocalDictationSession session)
    {
        _session = session;
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 14, 14) };
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(Copy("CLOUD · OPENAI WHISPER", 10, true));
        Grid.SetColumn(_status, 1); heading.Children.Add(_status); panel.Children.Add(heading);
        panel.Children.Add(Copy("Transcribe with Groq. Audio is sent after recording; internet and a Groq API key are required.", 13, true));

        var connection = new StackPanel { Spacing = 8 };
        var labelRow = new Grid();
        labelRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        labelRow.Children.Add(Copy("API key", 14));
        var getKey = Button("Get an API key ↗");
        getKey.Style = (Style)Application.Current.Resources["PrototypeIconButtonStyle"];
        getKey.Foreground = Brush("AccentBrush"); getKey.Padding = new Thickness(6, 0, 0, 0); getKey.MinHeight = 24;
        getKey.Click += async (_, _) => await global::Windows.System.Launcher.LaunchUriAsync(new Uri("https://console.groq.com/keys"));
        Grid.SetColumn(getKey, 1); labelRow.Children.Add(getKey); connection.Children.Add(labelRow);
        var input = new Grid(); input.Children.Add(_key);
        _placeholder.Margin = new Thickness(12, 0, 12, 0); _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false; AutomationProperties.SetAccessibilityView(_placeholder, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        input.Children.Add(_placeholder);
        var inputBorder = new Border { Child = input, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            BorderBrush = Brush("HairlineBrush"), Background = Brush("SurfaceBrush") };
        _key.GotFocus += (_, _) => inputBorder.BorderBrush = Brush("FocusBrush");
        _key.LostFocus += (_, _) => inputBorder.BorderBrush = Brush("HairlineBrush");
        AutomationProperties.SetName(_key, "Groq API key");
        AutomationProperties.SetHelpText(_key, "Enter a key to save it encrypted for your Windows user. An empty field keeps the saved key.");
        var inputRow = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        inputRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        inputRow.RowDefinitions.Add(new() { Height = GridLength.Auto }); inputRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
        inputRow.Children.Add(inputBorder); Grid.SetColumn(_save, 1); inputRow.Children.Add(_save);
        _save.VerticalAlignment = VerticalAlignment.Stretch; _save.MinWidth = 116;
        inputRow.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 420;
            Grid.SetColumnSpan(inputBorder, narrow ? 2 : 1);
            Grid.SetColumn(_save, narrow ? 0 : 1); Grid.SetRow(_save, narrow ? 1 : 0);
        };
        connection.Children.Add(inputRow);
        connection.Children.Add(Copy("Encrypted for your Windows user. Groq usage follows your account's plan.", 11, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_check); actions.Children.Add(_remove); connection.Children.Add(actions);
        connection.Children.Add(_feedback); panel.Children.Add(connection);
        panel.Children.Add(new Border { Height = 1, Background = Brush("HairlineBrush"), Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(Copy("Dictation model", 16)); panel.Children.Add(_models);
        panel.Children.Add(Copy("Language: Automatic or your selection in Dictation settings. Live preview is available with local models.", 11, true));
        Content = panel;
        AutomationProperties.SetLiveSetting(_feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _key.PasswordChanged += (_, _) => { _message = null; UpdateButtons(); };
        _save.Click += async (_, _) =>
        {
            var error = await session.SaveGroqKeyAsync(_key.Password);
            if (error is null) _key.Password = "";
            ShowResult(error);
        };
        _remove.Click += async (_, _) => { await PerformAsync(() => session.SaveGroqKeyAsync("")); _key.Password = ""; };
        _check.Click += async (_, _) => await PerformAsync(session.ValidateGroqAsync);
        Loaded += (_, _) => { session.Groq.Changed += Refresh; session.Changed += Refresh; Update(); };
        Unloaded += (_, _) => { session.Groq.Changed -= Refresh; session.Changed -= Refresh; _key.Password = ""; };
        Update();
    }
    private async Task PerformAsync(Func<Task<string?>> action) => ShowResult(await action());
    private void ShowResult(string? error) { _message = error; Update(); }
    private void Refresh() => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) Update(); });
    private void Update()
    {
        var groq = _session.Groq;
        _status.Text = groq.Busy ? "Working…" : !groq.Enabled ? "Not enabled" : groq.Ready ? "Key saved" : "Key required";
        _feedback.Text = _message ?? groq.Error ?? groq.Feedback ?? "";
        _feedback.Visibility = string.IsNullOrEmpty(_feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (_models.Children.Count == 0 || !_modelButtons.Select(m => m.Id).SequenceEqual(groq.Models.Select(m => m.Id)))
        {
            _models.Children.Clear(); _modelButtons.Clear();
            foreach (var model in groq.Models)
            {
                var row = new Grid { ColumnSpacing = 16, RowSpacing = 8, Padding = new Thickness(0, 10, 0, 12) };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var label = new StackPanel { Spacing = 4 };
                label.Children.Add(Copy(model.DisplayName, 14));
                label.Children.Add(Copy(model.Id.EndsWith("turbo", StringComparison.Ordinal) ? "Faster · multilingual" : "Higher accuracy · multilingual", 12, true));
                row.Children.Add(label);
                var use = Button("Use model");
                use.Click += async (_, _) => await PerformAsync(() => _session.SelectGroqModelAsync(model.Id));
                AutomationProperties.SetName(use, "Use Groq " + model.DisplayName);
                Grid.SetColumn(use, 1); use.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(use);
                row.SizeChanged += (_, e) =>
                {
                    var narrow = e.NewSize.Width < 380;
                    Grid.SetColumnSpan(label, narrow ? 2 : 1); Grid.SetColumn(use, narrow ? 0 : 1); Grid.SetRow(use, narrow ? 1 : 0);
                };
                _modelButtons.Add((model.Id, use)); _models.Children.Add(row);
            }
            if (groq.Models.Count == 0) _models.Children.Add(Copy("Save your key to enable Groq and choose a Whisper model.", 12, true));
        }
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var groq = _session.Groq;
        var available = _session.CanChangeProvider;
        _key.IsEnabled = available;
        _placeholder.Text = groq.Ready ? "Key saved · enter a replacement" : "Paste your Groq API key";
        _placeholder.Visibility = string.IsNullOrEmpty(_key.Password) ? Visibility.Visible : Visibility.Collapsed;
        _save.Content = groq.Enabled ? "Save key" : "Save & enable";
        _save.IsEnabled = available && !string.IsNullOrWhiteSpace(_key.Password);
        _check.Visibility = _remove.Visibility = groq.Ready ? Visibility.Visible : Visibility.Collapsed;
        _check.IsEnabled = _remove.IsEnabled = available && groq.Ready;
        foreach (var row in _modelButtons)
        {
            var active = _session.UsesGroq && groq.ModelId == row.Id;
            row.Button.Content = active ? "Active" : "Use model";
            row.Button.IsEnabled = available && groq.Ready && !active;
        }
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new() { Text = text, FontSize = size,
        TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "MutedBrush" : "TextBrush") };
    private static HandCursorButton Button(string text, bool primary = false) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
        Style = (Style)Application.Current.Resources[primary ? "PrototypePrimaryButtonStyle" : "PrototypeSecondaryButtonStyle"] };
}
