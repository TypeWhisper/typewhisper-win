using System.Collections.ObjectModel;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

public sealed partial class MarketplaceView : UserControl
{
    private IReadOnlyList<MarketplaceItem> _catalog = [];
    private LocalDictationSession? _runtime;
    private IReadOnlyList<PortableCatalogEntry> _entries = [];
    private bool _fetching;
    private bool _restarting;
    internal Func<Task<string?>>? RestartRequested { get; set; }
    private string? _error;
    private string? _operationMessage;
    private Func<string, bool> _isInstalled = _ => false;
    private MarketplaceItem? _opened;
    private string _query = string.Empty;
    private string _categoriesFilter = string.Empty;
    private bool _reviewing;
    private CancellationTokenSource? _installation;
    internal bool IsDetail { get; private set; }
    internal ObservableCollection<MarketplaceItem> FilteredItems { get; } = [];
    internal event EventHandler? InstalledRequested;
    private void Installed_Click(object sender, RoutedEventArgs e) => InstalledRequested?.Invoke(this, EventArgs.Empty);
    internal event EventHandler? ExitRequested;
    internal event EventHandler? LauncherRequested;
    internal event EventHandler? ClearSearchRequested;
    internal event Action<bool>? DetailModeChanged;
    internal event Action<string>? ManageRequested;

    public MarketplaceView()
    {
        InitializeComponent();
        EntryActionMenu.Attach(this, () => EntryActionMenu.FromButtons(ContextActionsFooter));
        IntegrationTabs.SetItems([new("installed", "Installed"), new("discover", "Discover")], "discover");
        IntegrationTabs.SelectionChanged += id =>
        { IntegrationTabs.SetSelected("discover"); if (id == "installed") Installed_Click(this, new RoutedEventArgs()); };
        CatalogFilters.SelectionChanged += id => { _categoriesFilter = id == "all" ? "" : id; Filter(_query); };
        UpdateCategories();
        Filter(string.Empty);
    }

    internal void ConfigureRuntime(LocalDictationSession runtime)
    {
        _runtime = runtime;
        runtime.Packages.Updates.Changed += () => DispatcherQueue.TryEnqueue(() => { UpdateAllAction(); if (!IsDetail) Filter(_query); else UpdateDetail(); });
        _isInstalled = runtime.Packages.Store.IsInstalled;

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
            _runtime.Packages.Updates.AcceptCatalog(_entries);
            _catalog = _entries.Select(entry => new MarketplaceItem(new(entry.Id, entry.Name, entry.Description,
                "plugin", string.Join(" / ", entry.Categories.Select(value => value == "llm" ? "LLM" : char.ToUpperInvariant(value[0]) + value[1..])), "Plugins run with your Windows user's permissions. Install only publishers you trust.",
                entry.Version, entry.MinHostVersion), entry.Author)
                { CategoriesIds = entry.Categories, Supported = entry.Supports(LocalCtcVocabulary.HostVersion, PortablePluginCatalog.Architecture) }).ToArray();
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
        finally { UpdateCategories(); _fetching = false; ResetFiltersButton.Visibility = Visibility.Visible; Filter(_query); }
    }

    internal void Filter(string query)
    {
        if (IsDetail) return;
        UpdateAllAction();
        _query = query;
        var selectedId = (MarketList.SelectedItem as MarketplaceItem)?.Plugin.Id ?? _opened?.Plugin.Id;
        FilteredItems.Clear();
        var available = _catalog.Where(item => !_isInstalled(item.Plugin.Id)).ToArray();
        foreach (var item in available.Where(item => (_categoriesFilter.Length == 0 || item.CategoriesIds.Contains(_categoriesFilter, StringComparer.OrdinalIgnoreCase))
            && (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Categories.Contains(query, StringComparison.OrdinalIgnoreCase))))
            FilteredItems.Add(item with { Installed = _isInstalled(item.Plugin.Id),
                UpdateAvailable = HasUpdate(item), PendingRestart = _runtime?.Packages.Store.PendingRestart(item.Plugin.Id) == true });
        MarketList.SelectedItem = FilteredItems.FirstOrDefault(item => item.Plugin.Id == selectedId) ?? FilteredItems.FirstOrDefault();
        MarketSummary.Text = $"{FilteredItems.Count} integrations";
        MarketEmptyState.Visibility = FilteredItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_catalog.Count > 0)
        {
            ResetFiltersButton.Content = available.Length == 0 ? "Refresh catalog" : "Reset filters";
            EmptyTitle.Text = available.Length == 0 ? "All available plugins are installed" : "No matching plugins";
            EmptyDescription.Text = available.Length == 0
                ? "Open an installed plugin in the sidebar to change its settings. Check again later for new plugins."
                : "Try another search or category.";
        }
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
        if (IsDetail) { if (MarketPrimaryButton.IsEnabled) Primary_Click(this, new RoutedEventArgs()); return; }
        if (MarketList.SelectedItem is not MarketplaceItem item) return;
        _opened = item; _error = null;
        IsDetail = true;
        UpdateAllAction();
        IntegrationTabs.Visibility = Visibility.Collapsed;
        MarketListPage.Visibility = Visibility.Collapsed;
        MarketDetailPage.Visibility = Visibility.Visible;
        DetailModeChanged?.Invoke(true);
        UpdateDetail();
        MarketDetailPage.ChangeView(null, 0, null, true);
        MarketPrimaryButton.Focus(FocusState.Programmatic);
    }

    private void Item_Click(object sender, ItemClickEventArgs e) { MarketList.SelectedItem = e.ClickedItem; OpenSelected(); }

    private bool HasUpdate(MarketplaceItem item) => _runtime?.Packages.Store.InstalledVersion(item.Plugin.Id) is { } current &&
        Version.TryParse(current, out var installed) && Version.TryParse(item.Plugin.Version, out var available) && available > installed;

    private void UpdateDetail(bool updateBreadcrumbs = true)
    {
        if (_opened is not { } item || _runtime is null) return;
        var installed = _isInstalled(item.Plugin.Id);
        var update = HasUpdate(item);
        var pending = _runtime.Packages.Store.PendingRestart(item.Plugin.Id);
        var busy = _installation is not null || _runtime.Packages.Updates.Busy;
        MarketTitle.Text = item.Title;
        MarketSummary.Text = "Discover";
        MarketDescription.Text = item.Description;
        MarketPublisher.Text = item.Publisher + " / " + item.Categories;
        MarketAccess.Text = item.Plugin.Permissions;
        MarketCompatibility.Text = $"Version {item.Plugin.Version} · Minimum host {item.Plugin.MinimumHostVersion}";
        MarketStatus.Text = busy ? "Installing…" : _error is not null ? pending ? "Restart unavailable" : "Installation failed" : pending ? "Restart required" : !item.Supported ? "Not compatible" : update ? "Update available" : installed ? "Installed" : "Available";
        MarketStatusExplanation.Text = _error ?? (busy ? _runtime.Packages.Updates.Busy ? _runtime.Packages.Updates.Status : _operationMessage ?? "Preparing installation…"
            : pending ? "Restart TypeWhisper to use the update. The current version remains available until then."
            : !item.Supported ? "This package does not support your TypeWhisper version or Windows architecture."
            : installed && !update ? "Open plugin settings to finish setup or manage its models."
            : "The package is downloaded over HTTPS and checked against the catalog checksum.");
        MarketPrimaryButton.Visibility = Visibility.Visible;
        MarketPrimaryButton.Content = _restarting ? "Restarting…" : busy ? "Installing…" : pending ? "Restart now · Enter" : update ? "Update" : installed ? "Open settings" : "Install";
        MarketPrimaryButton.IsEnabled = !_restarting && !busy && item.Supported && _runtime.Packages.Store.Initialized
            && (!pending || RestartRequested is not null);
        MarketCancelButton.Visibility = _installation is not null ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.Visibility = _installation is not null ? Visibility.Visible : Visibility.Collapsed;
        MarketNavigationHint.Text = busy ? "Esc Cancel" : "⌫ / Esc Back";
        if (updateBreadcrumbs) UpdateBreadcrumbs();
    }

    private bool _settingsLayout;
    internal void UseSettingsLayout()
    {
        _settingsLayout = true;
        IntegrationTabs.Visibility = Visibility.Collapsed;
        MarketTitle.Text = "Discover plugins";
        UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        var crumbs = new List<Crumb> { new(_settingsLayout ? "Settings" : "Quick Launch", () =>
        {
            ShowList(true);
            LauncherRequested?.Invoke(this, EventArgs.Empty);
        }, _settingsLayout ? "Marketplace breadcrumb Settings" : "Marketplace breadcrumb Quick Launch") };
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
        IntegrationTabs.Visibility = _settingsLayout ? Visibility.Collapsed : Visibility.Visible;
        MarketTitle.Text = _settingsLayout ? "Discover plugins" : "Integrations";
        MarketListPage.Visibility = Visibility.Visible;
        MarketDetailPage.Visibility = Visibility.Collapsed;
        MarketPrimaryButton.Visibility = MarketCancelButton.Visibility = Visibility.Collapsed;
        MarketNavigationHint.Text = "⌫ / Esc Back   ↑↓ Navigate   Enter Open";
        DetailModeChanged?.Invoke(false);
        if (reset)
        {
            _query = _categoriesFilter = string.Empty;
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
        if (_runtime is null || _opened is not { } item || _installation is not null || _runtime.Packages.Updates.Busy || _restarting) return;
        if (_runtime.Packages.Store.PendingRestart(item.Plugin.Id))
        {
            if (RestartRequested is null) return;
            _restarting = true; _error = null; UpdateDetail(false);
            try { _error = await RestartRequested(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _error = "Restart could not finish. Close and reopen TypeWhisper to apply the update."; }
            finally { _restarting = false; if (IsDetail) UpdateDetail(false); }
            return;
        }
        if (_isInstalled(item.Plugin.Id) && !HasUpdate(item)) { ManageRequested?.Invoke(item.Plugin.Id); return; }
        if (!item.Supported) return;
        if (_isInstalled(item.Plugin.Id) && HasUpdate(item))
        {
            await _runtime.Packages.Updates.UpdateAsync(item.Plugin.Id);
            if (IsDetail) UpdateDetail();
            return;
        }
        var entry = _entries.Single(entry => entry.Id == item.Plugin.Id);
        using var operation = new CancellationTokenSource();
        _installation = operation; _error = null; _operationMessage = "Preparing installation…"; SetProgress(0);
        UpdateDetail(updateBreadcrumbs: false);
        var openSettings = false;
        try
        {
            var restart = await _runtime.Packages.Store.InstallAsync(entry, new Progress<PluginInstallationProgress>(value =>
            {
                if (!ReferenceEquals(_installation, operation)) return;
                _operationMessage = value.Message;
                SetProgress(value.Fraction);
                if (IsDetail) MarketStatusExplanation.Text = value.Message;
            }), operation.Token);
            openSettings = !restart && !operation.IsCancellationRequested && ReferenceEquals(_installation, operation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _error = ex.Message; }
        finally
        {
            if (ReferenceEquals(_installation, operation))
            {
                _installation = null;
                if (openSettings)
                {
                    ShowList(true);
                    if (IsLoaded) ManageRequested?.Invoke(item.Plugin.Id);
                }
                else if (IsDetail) { UpdateDetail(); MarketPrimaryButton.Focus(FocusState.Programmatic); }
                else Filter(_query);
            }
        }
    }

    private void UpdateCategories()
    {
        var ids = _catalog.SelectMany(item => item.CategoriesIds).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (!ids.Contains(_categoriesFilter, StringComparer.OrdinalIgnoreCase)) _categoriesFilter = "";
        CatalogFilters.SetItems(new[] { new Tab("all", "All") }.Concat(ids.Select(id =>
            new Tab(id, id == "llm" ? "LLM" : char.ToUpperInvariant(id[0]) + id[1..]))).ToArray(),
            _categoriesFilter.Length == 0 ? "all" : _categoriesFilter);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e) { ShowList(true); await RefreshCatalogAsync(); }
    private void SetProgress(double? progress)
    {
        InstallProgress.IsIndeterminate = progress is null || !double.IsFinite(progress.Value);
        if (!InstallProgress.IsIndeterminate) InstallProgress.Value = Math.Clamp(progress!.Value, 0, 1);
    }
    internal void ResetNavigation() => ShowList(true);
}
