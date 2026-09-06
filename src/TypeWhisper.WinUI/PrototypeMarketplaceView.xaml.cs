using System.Collections.ObjectModel;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeMarketplaceView : UserControl
{
    private IReadOnlyList<PrototypeMarketplaceItem> _catalog = [];
    private LocalDictationSession? _runtime;
    private IReadOnlyList<PortableCatalogEntry> _entries = [];
    private bool _fetching;
    private string? _error;
    private string? _operationMessage;
    private Func<string, bool> _isInstalled = _ => false;
    private PrototypeMarketplaceItem? _opened;
    private string _query = string.Empty;
    private string _category = string.Empty;
    private bool _reviewing;
    private CancellationTokenSource? _installation;
    internal bool IsDetail { get; private set; }
    internal ObservableCollection<PrototypeMarketplaceItem> FilteredItems { get; } = [];
    internal event EventHandler? InstalledRequested;
    private void Installed_Click(object sender, RoutedEventArgs e) => InstalledRequested?.Invoke(this, EventArgs.Empty);
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

    internal void ConfigureRuntime(LocalDictationSession runtime)
    {
        _runtime = runtime;
        _isInstalled = runtime.Packages.Store.IsInstalled;
        CatalogFilters.Visibility = Visibility.Collapsed;
        ResetFiltersButton.Content = "Retry";
        Loaded += async (_, _) => await RefreshCatalogAsync();
        runtime.Changed += () => DispatcherQueue.TryEnqueue(() => { if (IsDetail) UpdateDetail(); else Filter(_query); });
    }

    private async Task RefreshCatalogAsync()
    {
        if (_runtime is null || _fetching || _installation is not null) return;
        _fetching = true; _error = null;
        EmptyTitle.Text = "Loading integrations…";
        EmptyDescription.Text = "";
        ResetFiltersButton.Visibility = Visibility.Collapsed;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _entries = await _runtime.Packages.Catalog.FetchAsync(timeout.Token);
            _catalog = _entries.Select(entry => new PrototypeMarketplaceItem(new(entry.Id, entry.Name, entry.Description,
                "plugin", entry.Category, "Plugins run with your Windows user's permissions. Install only publishers you trust.",
                entry.Version, entry.MinHostVersion), entry.Author)
                { Supported = entry.Supports(LocalCtcVocabulary.HostVersion, PortablePluginCatalog.Architecture) }).ToArray();
            EmptyTitle.Text = _catalog.Count == 0 ? "No integrations published yet" : "No matching integrations";
            EmptyDescription.Text = _catalog.Count == 0 ? "The catalog is ready. Plugins will appear here when they are published." : "Try another search.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _catalog = []; _entries = [];
            EmptyTitle.Text = "Catalog unavailable";
            EmptyDescription.Text = "The plugin catalog could not be loaded. Your installed plugins remain available. Try again shortly.";
            System.Diagnostics.Debug.WriteLine("Plugin catalog: " + ex.GetType().Name);
        }
        finally { _fetching = false; ResetFiltersButton.Visibility = Visibility.Visible; Filter(_query); }
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
            FilteredItems.Add(item with { Installed = _isInstalled(item.Plugin.Id),
                UpdateAvailable = HasUpdate(item), PendingRestart = _runtime?.Packages.Store.PendingRestart(item.Plugin.Id) == true });
        MarketList.SelectedItem = FilteredItems.FirstOrDefault(item => item.Plugin.Id == selectedId) ?? FilteredItems.FirstOrDefault();
        MarketSummary.Text = $"{FilteredItems.Count} integrations";
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
        _opened = item; _error = null;
        IsDetail = true;
        IntegrationTabs.Visibility = Visibility.Collapsed;
        MarketListPage.Visibility = Visibility.Collapsed;
        MarketDetailPage.Visibility = Visibility.Visible;
        DetailModeChanged?.Invoke(true);
        UpdateDetail();
        MarketDetailPage.ChangeView(null, 0, null, true);
        MarketPrimaryButton.Focus(FocusState.Programmatic);
    }

    private void Item_Click(object sender, ItemClickEventArgs e) { MarketList.SelectedItem = e.ClickedItem; OpenSelected(); }

    private bool HasUpdate(PrototypeMarketplaceItem item) => _runtime?.Packages.Store.InstalledVersion(item.Plugin.Id) is { } current &&
        Version.TryParse(current, out var installed) && Version.TryParse(item.Plugin.Version, out var available) && available > installed;

    private void UpdateDetail(bool updateBreadcrumbs = true)
    {
        if (_opened is not { } item || _runtime is null) return;
        var installed = _isInstalled(item.Plugin.Id);
        var update = HasUpdate(item);
        var pending = _runtime.Packages.Store.PendingRestart(item.Plugin.Id);
        var busy = _installation is not null;
        MarketTitle.Text = item.Title;
        MarketSummary.Text = "Discover";
        MarketDescription.Text = item.Description;
        MarketPublisher.Text = item.Publisher;
        MarketAccess.Text = item.Plugin.Permissions;
        MarketCompatibility.Text = $"Version {item.Plugin.Version} · Minimum host {item.Plugin.MinimumHostVersion}";
        MarketStatus.Text = busy ? "Installing…" : _error is not null ? "Installation failed" : pending ? "Restart required" : !item.Supported ? "Not compatible" : update ? "Update available" : installed ? "Installed" : "Available";
        MarketStatusExplanation.Text = _error ?? (busy ? _operationMessage ?? "Preparing installation…"
            : pending ? "Restart TypeWhisper to use the update. The current version remains available until then."
            : !item.Supported ? "This package does not support your TypeWhisper version or Windows architecture."
            : installed && !update ? "Open plugin settings to finish setup or manage its models."
            : "The package is downloaded over HTTPS and checked against the catalog checksum.");
        MarketPrimaryButton.Visibility = Visibility.Visible;
        MarketPrimaryButton.Content = busy ? "Installing…" : pending ? "Restart required" : update ? "Update" : installed ? "Open in Installed" : "Install";
        MarketPrimaryButton.IsEnabled = !busy && !pending && item.Supported && _runtime.Packages.Store.Initialized;
        MarketCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MarketNavigationHint.Text = busy ? "Esc Cancel" : "⌫ / Esc Back";
        if (updateBreadcrumbs) UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        var crumbs = new List<PrototypeCrumb> { new("Quick Launch", () =>
        {
            ShowList(true);
            LauncherRequested?.Invoke(this, EventArgs.Empty);
        }, "Marketplace breadcrumb Quick Launch") };
        if (!IsDetail) crumbs.Add(new("Integrations"));
        else
        {
            crumbs.Add(new("Integrations", () => ShowList(true), "Marketplace breadcrumb catalog"));
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
        IntegrationTabs.Visibility = Visibility.Visible;
        MarketTitle.Text = "Integrations";
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
        if (_runtime is null || _opened is not { } item || _installation is not null) return;
        if (_isInstalled(item.Plugin.Id) && !HasUpdate(item)) { ManageRequested?.Invoke(item.Plugin.Id); return; }
        if (!item.Supported) return;
        var entry = _entries.Single(entry => entry.Id == item.Plugin.Id);
        using var operation = new CancellationTokenSource();
        _installation = operation; _error = null; _operationMessage = "Preparing installation…"; SetProgress(0);
        UpdateDetail(updateBreadcrumbs: false);
        try
        {
            var restart = await _runtime.Packages.Store.InstallAsync(entry, new Progress<PluginInstallationProgress>(value =>
            {
                if (!ReferenceEquals(_installation, operation)) return;
                _operationMessage = value.Message;
                SetProgress(value.Fraction);
                if (IsDetail) MarketStatusExplanation.Text = value.Message;
            }), operation.Token);
            if (!restart && ReferenceEquals(_installation, operation)) ManageRequested?.Invoke(item.Plugin.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _error = ex.Message; }
        finally
        {
            if (ReferenceEquals(_installation, operation))
            {
                _installation = null;
                if (IsDetail) { UpdateDetail(); MarketPrimaryButton.Focus(FocusState.Programmatic); }
                else Filter(_query);
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

    private async void Reset_Click(object sender, RoutedEventArgs e) { ShowList(true); await RefreshCatalogAsync(); }
    private void SetProgress(double? progress)
    {
        InstallProgress.IsIndeterminate = progress is null || !double.IsFinite(progress.Value);
        if (!InstallProgress.IsIndeterminate) InstallProgress.Value = Math.Clamp(progress!.Value, 0, 1);
    }
    internal void ResetNavigation() => ShowList(true);
}
