using System.Windows;

namespace TypeWhisper.Plugin.Webhook;

public sealed class ProfileFilterItem(string name, bool isSelected = false)
{
    public string Name { get; } = name;
    public bool IsSelected { get; set; } = isSelected;
}

public partial class WebhookEditWindow : Window
{
    private readonly Guid _editId;
    private readonly bool _editIsEnabled;
    private readonly Dictionary<string, string> _editHeaders;
    private readonly List<ProfileFilterItem> _profileItems;

    public WebhookConfig? Result { get; private set; }

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
            : "Nur aktiv für ausgewählte Profile";
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
