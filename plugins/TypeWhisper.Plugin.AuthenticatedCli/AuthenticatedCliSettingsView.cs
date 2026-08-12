using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal sealed class AuthenticatedCliSettingsView : UserControl
{
    private readonly AuthenticatedCliPlugin _plugin;
    private readonly Dictionary<string, ProviderCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _refreshButton;
    private bool _updating;

    internal AuthenticatedCliSettingsView(AuthenticatedCliPlugin plugin)
    {
        _plugin = plugin;
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 4), MaxWidth = 620 };
        root.Children.Add(new TextBlock
        {
            Text = L("Settings.Title"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        root.Children.Add(new TextBlock
        {
            Text = L("Settings.Description"),
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        });

        _refreshButton = new Button
        {
            Content = L("Settings.Refresh"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetAutomationId(_refreshButton, "AuthenticatedCliRefresh");
        _refreshButton.Click += OnRefreshClick;
        root.Children.Add(_refreshButton);

        foreach (var descriptor in CliProviderDescriptor.All)
        {
            var card = CreateProviderCard(descriptor);
            _cards[descriptor.Key] = card;
            root.Children.Add(card.Container);
        }

        Content = root;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private ProviderCard CreateProviderCard(CliProviderDescriptor descriptor)
    {
        var title = new TextBlock
        {
            Text = L(descriptor.DisplayKey),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };
        var state = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(state, $"AuthenticatedCliState_{descriptor.Key}");
        var path = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brushes.Gray,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var version = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = Brushes.Gray,
            FontSize = 11
        };
        var checkedAt = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = Brushes.Gray,
            FontSize = 11
        };
        var pickerLabel = new TextBlock
        {
            Text = L("Settings.SelectExecutable"),
            Margin = new Thickness(0, 8, 0, 3),
            Foreground = Brushes.LightGray,
            Visibility = Visibility.Collapsed
        };
        var picker = new ComboBox
        {
            Visibility = Visibility.Collapsed,
            FontFamily = new FontFamily("Consolas")
        };
        AutomationProperties.SetAutomationId(picker, $"AuthenticatedCliExecutable_{descriptor.Key}");
        picker.SelectionChanged += async (_, _) =>
        {
            if (_updating || picker.SelectedItem is not string selected)
                return;
            await _plugin.SelectExecutableAsync(descriptor, selected);
            UpdateCards();
        };
        var help = new TextBlock
        {
            Text = L("Settings.InstallHelp"),
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var docsButton = new Button
        {
            Content = L("Settings.OpenDocs"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Tag = descriptor.DocumentationUrl
        };
        docsButton.Click += OnOpenDocsClick;

        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(state);
        content.Children.Add(path);
        content.Children.Add(version);
        content.Children.Add(checkedAt);
        content.Children.Add(pickerLabel);
        content.Children.Add(picker);
        content.Children.Add(help);
        content.Children.Add(docsButton);

        var container = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x3B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = content
        };

        return new ProviderCard(descriptor, container, state, path, version, checkedAt, pickerLabel, picker);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _plugin.AvailabilityChanged += OnAvailabilityChanged;
        UpdateCards();
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        _plugin.AvailabilityChanged -= OnAvailabilityChanged;

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        _refreshButton.IsEnabled = false;
        try
        {
            await _plugin.RefreshFromSettingsAsync();
            UpdateCards();
        }
        finally
        {
            _refreshButton.IsEnabled = true;
        }
    }

    private void OnAvailabilityChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateCards);
            return;
        }

        UpdateCards();
    }

    private void UpdateCards()
    {
        _updating = true;
        try
        {
            foreach (var card in _cards.Values)
            {
                var snapshot = _plugin.GetSnapshot(card.Descriptor);
                card.State.Text = L($"State.{snapshot.State}");
                card.State.Foreground = snapshot.State == CliAvailabilityState.Ready
                    ? Brushes.LightGreen
                    : snapshot.State is CliAvailabilityState.Checking
                        ? Brushes.LightGray
                        : Brushes.Orange;
                card.Path.Text = snapshot.ExecutablePath is null
                    ? ""
                    : $"{L("Settings.Executable")}: {snapshot.ExecutablePath}";
                card.Version.Text = snapshot.Version is null ? "" : L("Settings.Version", snapshot.Version);
                card.CheckedAt.Text = snapshot.CheckedAt == DateTimeOffset.MinValue
                    ? ""
                    : L("Settings.LastChecked", snapshot.CheckedAt.ToLocalTime().ToString("g"));

                var showPicker = snapshot.Candidates.Count > 1
                                 || snapshot.State == CliAvailabilityState.SelectedExecutableMissing;
                card.PickerLabel.Visibility = showPicker ? Visibility.Visible : Visibility.Collapsed;
                card.Picker.Visibility = showPicker ? Visibility.Visible : Visibility.Collapsed;
                card.Picker.ItemsSource = snapshot.Candidates;
                card.Picker.SelectedItem = snapshot.Candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate, snapshot.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private static void OnOpenDocsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Opening documentation is optional and must not affect provider state.
        }
    }

    private string L(string key, params object[] args) => _plugin.GetString(key, args);

    private sealed record ProviderCard(
        CliProviderDescriptor Descriptor,
        Border Container,
        TextBlock State,
        TextBlock Path,
        TextBlock Version,
        TextBlock CheckedAt,
        TextBlock PickerLabel,
        ComboBox Picker);
}
