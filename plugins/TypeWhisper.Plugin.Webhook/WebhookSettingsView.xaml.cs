using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TypeWhisper.Plugin.Webhook;

/// <summary>
/// Provides bool to icon converter behavior.
/// </summary>
public sealed class BoolToIconConverter : IValueConverter
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static readonly BoolToIconConverter Instance = new();

    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "\u2713" : "\u2717";

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Provides webhook settings view behavior.
/// </summary>
public partial class WebhookSettingsView : UserControl
{
    private readonly WebhookPlugin _plugin;

    /// <summary>
    /// Initializes a new instance of the WebhookSettingsView class.
    /// </summary>
    public WebhookSettingsView(WebhookPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        if (_plugin.Service is { } service)
        {
            WebhookList.ItemsSource = service.Webhooks;
            LogList.ItemsSource = service.DeliveryLog;

            service.Webhooks.CollectionChanged += OnWebhooksChanged;
            service.DeliveryLog.CollectionChanged += OnLogChanged;
        }

        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        var service = _plugin.Service;
        EmptyState.Visibility = service is null || service.Webhooks.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyLogState.Visibility = service is null || service.DeliveryLog.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWebhooksChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyStates();
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyStates();

    private void OnAddWebhook(object sender, RoutedEventArgs e)
    {
        var profiles = _plugin.Host?.AvailableProfileNames ?? [];
        var dialog = new WebhookEditWindow(profiles) { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() == true && dialog.Result is { } config)
            _plugin.Service?.AddWebhook(config);
    }

    private void OnEditWebhook(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var existing = _plugin.Service?.Webhooks.FirstOrDefault(w => w.Id == id);
        if (existing is null) return;

        var profiles = _plugin.Host?.AvailableProfileNames ?? [];
        var dialog = new WebhookEditWindow(profiles, existing) { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() == true && dialog.Result is { } updated)
            _plugin.Service?.UpdateWebhook(updated);
    }

    private void OnDeleteWebhook(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        _plugin.Service?.RemoveWebhook(id);
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Guid id, IsChecked: var isChecked }) return;
        var existing = _plugin.Service?.Webhooks.FirstOrDefault(w => w.Id == id);
        if (existing is null) return;

        _plugin.Service?.UpdateWebhook(existing with { IsEnabled = isChecked == true });
    }
}
