using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeMarketplaceView : UserControl
{
    private readonly IReadOnlyList<PrototypeMarketplaceItem> _catalog = PrototypeMarketplaceItem.Samples;
    private Func<string, bool> _isInstalled = _ => false;
    private Action<PrototypePlugin>? _install;
    private PrototypeMarketplaceItem? _opened;
    private string _query = string.Empty;
    private string _category = string.Empty;
    private bool _reviewing;
    private CancellationTokenSource? _installation;
    private double _progress;
    internal bool IsDetail { get; private set; }
    internal ObservableCollection<PrototypeMarketplaceItem> FilteredItems { get; } = [];
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event EventHandler? ClearSearchRequested;
    internal event Action<bool>? DetailModeChanged;
    internal event Action<string>? ManageRequested;

    public PrototypeMarketplaceView()
    {
        InitializeComponent();
        UpdateCategories();
        Filter(string.Empty);
    }

    internal void ConfigureInventory(Func<string, bool> isInstalled, Action<PrototypePlugin> install)
    {
        _isInstalled = isInstalled;
        _install = install;
        Filter(_query);
    }

    internal void Filter(string query)
    {
        if (IsDetail) return;
        _query = query;
        var selectedId = (MarketList.SelectedItem as PrototypeMarketplaceItem)?.Plugin.Id ?? _opened?.Plugin.Id;
        FilteredItems.Clear();
        foreach (var item in _catalog.Where(item => item.Category.StartsWith(_category, StringComparison.OrdinalIgnoreCase)
            && (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase))))
            FilteredItems.Add(item with { Installed = _isInstalled(item.Plugin.Id) });
        MarketList.SelectedItem = FilteredItems.FirstOrDefault(item => item.Plugin.Id == selectedId) ?? FilteredItems.FirstOrDefault();
        MarketSummary.Text = $"{FilteredItems.Count} plugins · sample catalog";
        MarketEmptyState.Visibility = FilteredItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBreadcrumbs();
    }

    internal void MoveSelection(int delta)
    {
        if (IsDetail || FilteredItems.Count == 0) return;
        MarketList.SelectedIndex = Math.Clamp(MarketList.SelectedIndex + delta, 0, FilteredItems.Count - 1);
        MarketList.ScrollIntoView(MarketList.SelectedItem);
    }

    internal void OpenSelected()
    {
        if (IsDetail || MarketList.SelectedItem is not PrototypeMarketplaceItem item) return;
        _opened = item;
        IsDetail = true;
        MarketListPage.Visibility = Visibility.Collapsed;
        MarketDetailPage.Visibility = Visibility.Visible;
        DetailModeChanged?.Invoke(true);
        UpdateDetail();
        MarketDetailPage.ChangeView(null, 0, null, true);
        MarketPrimaryButton.Focus(FocusState.Programmatic);
    }

    private void Item_Click(object sender, ItemClickEventArgs e) { MarketList.SelectedItem = e.ClickedItem; OpenSelected(); }

    private void UpdateDetail(bool updateBreadcrumbs = true)
    {
        if (_opened is not { } item) return;
        var installed = _isInstalled(item.Plugin.Id);
        var busy = _installation is not null;
        var blocked = !item.Trusted || !item.Plugin.Compatible;
        MarketTitle.Text = _reviewing ? "Review installation" : item.Title;
        MarketSummary.Text = "Demo only";
        MarketDescription.Text = _reviewing ? $"Add {item.Title} to your example plugins?" : item.Description;
        MarketPublisher.Text = item.Publisher;
        MarketAccess.Text = item.Plugin.Permissions;
        MarketCompatibility.Text = $"Version {item.Plugin.Version} · Minimum host {item.Plugin.MinimumHostVersion} · "
            + (item.Trusted ? "Sample signature: valid" : "Sample signature: unavailable");
        MarketStatus.Text = busy ? "Simulating installation…" : installed ? "Installed in this session" : !item.Trusted ? "Installation blocked"
            : !item.Plugin.Compatible ? "Host update required" : _reviewing ? "Review requested access" : "Available to try";
        MarketStatus.Foreground = blocked ? new SolidColorBrush(Colors.Orange) : (Brush)Application.Current.Resources["AccentBrush"];
        MarketStatusExplanation.Text = busy ? "No download is running. Cancel or go back to stop this preview."
            : installed ? "Open this example in Plugins to change its settings or disable it."
            : !item.Trusted ? "The signature fixture is untrusted. This example cannot be installed."
            : !item.Plugin.Compatible ? $"This example requires TypeWhisper {item.Plugin.MinimumHostVersion} or later. The prototype represents host 1.1."
            : _reviewing ? "Confirm only after reviewing the access below. This adds an in-memory example, not an executable plugin."
            : "Inspect its requested access before adding it to your example plugins.";
        MarketPrimaryButton.Visibility = Visibility.Visible;
        MarketPrimaryButton.Content = installed ? "Open in Plugins" : busy ? "Installing…" : _reviewing ? "Confirm demo install" : "Install example";
        MarketPrimaryButton.IsEnabled = !busy && (installed || !blocked);
        MarketCancelButton.Visibility = _reviewing || busy ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MarketNavigationHint.Text = _reviewing || busy ? "Esc Cancel" : "⌫ / Esc Back";
        if (updateBreadcrumbs) UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        var crumbs = new List<PrototypeCrumb> { new("Quick Launch", () =>
        {
            ShowList(true);
            LauncherRequested?.Invoke(this, EventArgs.Empty);
        }, "Marketplace breadcrumb Quick Launch") };
        if (!IsDetail) crumbs.Add(new("Marketplace"));
        else
        {
            crumbs.Add(new("Marketplace", () => ShowList(true), "Marketplace breadcrumb catalog"));
            if (_reviewing)
            {
                crumbs.Add(new(_opened?.Title ?? "Plugin", CancelInstall, "Marketplace breadcrumb detail"));
                crumbs.Add(new("Install"));
            }
            else crumbs.Add(new(_opened?.Title ?? "Plugin"));
        }
        MarketBreadcrumbs.SetItems(crumbs.ToArray());
    }

    private void ShowList(bool reset)
    {
        CancelPending();
        _reviewing = false;
        IsDetail = false;
        MarketTitle.Text = "Marketplace";
        MarketListPage.Visibility = Visibility.Visible;
        MarketDetailPage.Visibility = Visibility.Collapsed;
        MarketPrimaryButton.Visibility = MarketCancelButton.Visibility = Visibility.Collapsed;
        MarketNavigationHint.Text = "⌫ / Esc Back   ↑↓ Navigate   Enter Open";
        DetailModeChanged?.Invoke(false);
        if (reset)
        {
            _query = _category = string.Empty;
            UpdateCategories();
            ClearSearchRequested?.Invoke(this, EventArgs.Empty);
        }
        Filter(_query);
        MarketList.Focus(FocusState.Programmatic);
    }

    internal void GoBack()
    {
        if (_reviewing || _installation is not null) CancelInstall();
        else if (IsDetail) ShowList(false);
        else ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelPending()
    {
        _installation?.Cancel();
        _installation = null;
    }

    private void CancelInstall()
    {
        CancelPending();
        _reviewing = false;
        UpdateDetail();
        MarketPrimaryButton.Focus(FocusState.Programmatic);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelInstall();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_opened is not { } item || _installation is not null) return;
        if (_isInstalled(item.Plugin.Id)) { ManageRequested?.Invoke(item.Plugin.Id); return; }
        if (!item.Trusted || !item.Plugin.Compatible || _install is null) return;
        if (!_reviewing)
        {
            _reviewing = true;
            UpdateDetail();
            MarketDetailPage.ChangeView(null, 0, null, true);
            return;
        }
        using var operation = new CancellationTokenSource();
        _installation = operation;
        SetProgress(0.15);
        // The navigation path is unchanged while progress begins. Keep its
        // controls stable for both pointer input and accessibility clients.
        UpdateDetail(updateBreadcrumbs: false);
        MarketCancelButton.Focus(FocusState.Programmatic);
        try
        {
            // Leave enough time to inspect and cancel this intentionally fake
            // progress preview; this is not an installation-speed benchmark.
            await Task.Delay(1500, operation.Token);
            SetProgress(0.65);
            await Task.Delay(1500, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _install(item.Plugin);
            _reviewing = false;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_installation, operation))
            {
                _installation = null;
                UpdateDetail();
                MarketPrimaryButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        _category = (string?)((Button)sender).Tag ?? string.Empty;
        UpdateCategories();
        Filter(_query);
    }

    private void UpdateCategories()
    {
        foreach (var button in new[] { AllCategory, TranscriptionCategory, ExportCategory })
        {
            var selected = ((string?)button.Tag ?? string.Empty) == _category;
            button.Style = (Style)Application.Current.Resources[selected ? "PrototypePrimaryButtonStyle" : "PrototypeIconButtonStyle"];
            AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => ShowList(true);
    private void SetProgress(double progress)
    {
        _progress = progress;
        InstallProgressFill.Width = InstallProgress.ActualWidth * progress;
    }
    private void Progress_SizeChanged(object sender, SizeChangedEventArgs e) => SetProgress(_progress);
    internal void ResetNavigation() => ShowList(true);
}
