using System.Windows;

namespace TypeWhisper.Plugin.Webhook;

/// <summary>
/// Provides profile filter item behavior.
/// </summary>
public sealed class ProfileFilterItem(string name, bool isSelected = false)
{
    /// <summary>
    /// Gets the display or storage name.
    /// </summary>
    public string Name { get; } = name;
    /// <summary>
    /// Gets or sets the is selected value.
    /// </summary>
    public bool IsSelected { get; set; } = isSelected;
}

/// <summary>
/// Provides webhook edit window behavior.
/// </summary>
public partial class WebhookEditWindow : Window
{
    private readonly Guid _editId;
    private readonly bool _editIsEnabled;
    private readonly Dictionary<string, string> _editHeaders;
    private readonly List<ProfileFilterItem> _profileItems;

    /// <summary>
    /// Gets or sets the result value.
    /// </summary>
    public WebhookConfig? Result { get; private set; }

    /// <summary>
    /// Initializes a new instance of the WebhookEditWindow class.
    /// </summary>
    public WebhookEditWindow(IReadOnlyList<string> availableProfiles, WebhookConfig? existing = null)
    {
        InitializeComponent();

        _editId = existing?.Id ?? Guid.NewGuid();
        _editIsEnabled = existing?.IsEnabled ?? true;
        _editHeaders = existing?.Headers != null ? new(existing.Headers) : [];

        var selectedProfiles = existing?.ProfileFilter ?? [];
        _profileItems = availableProfiles
            .Select(p => new ProfileFilterItem(p, selectedProfiles.Contains(p)))
            .ToList();

        ProfileList.ItemsSource = _profileItems;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            UrlBox.Text = existing.Url;
            MethodCombo.SelectedIndex = existing.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        UpdateFilterHint();
    }

    private void UpdateFilterHint()
    {
        var selected = _profileItems.Count(p => p.IsSelected);
        FilterHint.Text = selected == 0
            ? "Aktiv für alle Transkriptionen"
            : "Nur aktiv für ausgewählte Workflows";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var url = UrlBox.Text.Trim();

        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("Bitte eine URL eingeben.", "Webhook", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(name))
            name = url;

        var method = MethodCombo.SelectedIndex == 1 ? "PUT" : "POST";
        var profileFilter = _profileItems.Where(p => p.IsSelected).Select(p => p.Name).ToList();

        Result = new WebhookConfig
        {
            Id = _editId,
            Name = name,
            Url = url,
            HttpMethod = method,
            Headers = _editHeaders,
            IsEnabled = _editIsEnabled,
            ProfileFilter = profileFilter
        };

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
