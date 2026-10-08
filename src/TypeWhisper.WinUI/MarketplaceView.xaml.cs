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
    internal bool IsInstalling => _installation is not null;
    private bool IsPageVisible()
    {
        if (!IsLoaded) return false;
        for (DependencyObject? element = this; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
    internal ObservableCollection<MarketplaceItem> FilteredItems { get; } = [];
    internal event EventHandler? ClearSearchRequested;
    internal event Action<bool>? DetailModeChanged;
    internal event Action<string>? ManageRequested;

    public MarketplaceView()
    {
        InitializeComponent();
        MarketTitle.Text = Loc.T("Discover plugins");
        MarketSummary.Text = Loc.T("Discover");
        AutomationProperties.SetName(MarketList, Loc.T("Available plugins"));
        EmptyTitle.Text = Loc.T("No matching plugins");
        ResetFiltersButton.Content = Loc.T("Reset filters");
        AutomationProperties.SetName(ResetFiltersButton, Loc.T("Reset marketplace filters"));
        AutomationProperties.SetName(InstallProgress, Loc.T("Plugin installation progress"));
        MarketAccessHeading.Text = Loc.T("REQUESTED ACCESS");
        MarketNavigationHint.Text = Loc.T("Esc Back   ↑↓ Navigate   Enter Open");
        MarketUpdateAllButton.Content = Loc.T("Update all");
        MarketCancelButton.Content = Loc.T("Cancel");
        AutomationProperties.SetName(MarketCancelButton, Loc.T("Cancel installation"));
        MarketPrimaryButton.Content = Loc.T("Install");
        AutomationProperties.SetName(MarketPrimaryButton, Loc.T("Marketplace primary action"));
        EntryActionMenu.Attach(this, () => EntryActionMenu.FromButtons(ContextActionsFooter));
        CatalogFilters.SelectionChanged += id => { _categoriesFilter = id == "all" ? "" : id; Filter(_query); };
        UpdateCategories();
        Filter(string.Empty);
    }

    internal void ConfigureRuntime(LocalDictationSession runtime)
    {
        _runtime = runtime;
        runtime.Packages.Updates.Changed += () => DispatcherQueue.TryEnqueue(() => { UpdateAllAction(); if (!IsDetail) Filter(_query); else UpdateDetail(); });
        _isInstalled = runtime.Packages.Store.IsInstalled;

        ResetFiltersButton.Content = Loc.T("Retry");
        Loaded += async (_, _) => await RefreshCatalogAsync();
        runtime.Changed += () => DispatcherQueue.TryEnqueue(() => { if (IsDetail) UpdateDetail(); else Filter(_query); });
    }

    private async Task RefreshCatalogAsync()
    {
        if (_runtime is null || _fetching || _installation is not null) return;
        _fetching = true; _error = null;
        EmptyTitle.Text = Loc.T("Loading plugins…");
        EmptyDescription.Text = "";
        ResetFiltersButton.Visibility = Visibility.Collapsed;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _entries = await _runtime.Packages.Catalog.FetchAsync(timeout.Token);
            _runtime.Packages.Updates.AcceptCatalog(_entries);
            _catalog = _entries.Select(entry => new MarketplaceItem(new(entry.Id, entry.Name, entry.Description,
                "plugin", string.Join(" / ", entry.Categories.Select(value => value == "llm" ? "LLM" : char.ToUpperInvariant(value[0]) + value[1..])), Loc.T("Plugins run with your Windows user's permissions. Install only publishers you trust."),
                entry.Version, entry.MinHostVersion), entry.Author)
                { CategoriesIds = entry.Categories, Supported = entry.Supports(LocalCtcVocabulary.HostVersion, PortablePluginCatalog.Architecture) }).ToArray();
            EmptyTitle.Text = _catalog.Count == 0 ? Loc.T("No plugins published yet") : Loc.T("No matching plugins");
            EmptyDescription.Text = _catalog.Count == 0 ? Loc.T("The catalog is ready. Plugins will appear here when they are published.") : Loc.T("Try another search.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _catalog = []; _entries = [];
            EmptyTitle.Text = Loc.T("Catalog unavailable");
            EmptyDescription.Text = Loc.T("The plugin catalog could not be loaded. Your installed plugins remain available. Try again shortly.");
            AppDiagnostics.Write("plugin.catalog.failed", ex);
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
        MarketSummary.Text = FilteredItems.Count == 1 ? Loc.T("1 plugin") : Loc.T("{0} plugins", FilteredItems.Count);
        MarketEmptyState.Visibility = FilteredItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_catalog.Count > 0)
        {
            ResetFiltersButton.Content = available.Length == 0 ? Loc.T("Refresh catalog") : Loc.T("Reset filters");
            EmptyTitle.Text = available.Length == 0 ? Loc.T("All available plugins are installed") : Loc.T("No matching plugins");
            EmptyDescription.Text = available.Length == 0
                ? Loc.T("Open an installed plugin in the sidebar to change its settings. Check again later for new plugins.")
                : Loc.T("Try another search or category.");
        }
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

    private void UpdateDetail()
    {
        if (_opened is not { } item || _runtime is null) return;
        var installed = _isInstalled(item.Plugin.Id);
        var update = HasUpdate(item);
        var pending = _runtime.Packages.Store.PendingRestart(item.Plugin.Id);
        var busy = _installation is not null || _runtime.Packages.Updates.Busy;
        MarketTitle.Text = item.Title;
        MarketSummary.Text = Loc.T("Discover");
        MarketDescription.Text = item.Description;
        MarketPublisher.Text = item.Publisher + " / " + item.Categories;
        MarketAccess.Text = item.Plugin.Permissions;
        MarketCompatibility.Text = Loc.T("Version {0} · Minimum host {1}", item.Plugin.Version, item.Plugin.MinimumHostVersion);
        MarketStatus.Text = busy ? Loc.T("Installing…") : _error is not null ? pending ? Loc.T("Restart unavailable") : Loc.T("Installation failed") : pending ? Loc.T("Restart required") : !item.Supported ? Loc.T("Not compatible") : update ? Loc.T("Update available") : installed ? Loc.T("Installed") : Loc.T("Available");
        MarketStatusExplanation.Text = _error ?? (busy ? _runtime.Packages.Updates.Busy ? PluginUpdateStatus.Text(_runtime.Packages.Updates) : _operationMessage ?? Loc.T("Preparing installation…")
            : pending ? Loc.T("Restart TypeWhisper to use the update. The current version remains available until then.")
            : !item.Supported ? Loc.T("This package does not support your TypeWhisper version or Windows architecture.")
            : installed && !update ? Loc.T("Open plugin settings to finish setup or manage its models.")
            : Loc.T("The package is downloaded over HTTPS and checked against the catalog checksum."));
        MarketPrimaryButton.Visibility = Visibility.Visible;
        MarketPrimaryButton.Content = _restarting ? Loc.T("Restarting…") : busy ? Loc.T("Installing…") : pending ? Loc.T("Restart now · Enter") : update ? Loc.T("Update") : installed ? Loc.T("Open settings") : Loc.T("Install");
        MarketPrimaryButton.IsEnabled = !_restarting && !busy && item.Supported && _runtime.Packages.Store.Initialized
            && (!pending || RestartRequested is not null);
        MarketCancelButton.Visibility = _installation is not null ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.Visibility = _installation is not null ? Visibility.Visible : Visibility.Collapsed;
        MarketNavigationHint.Text = busy ? Loc.T("Esc Cancel") : Loc.T("Esc Back");
    }

    private void ShowList(bool reset)
    {
        CancelPending();
        _reviewing = false;
        IsDetail = false;
        MarketListPage.Visibility = Visibility.Visible;
        MarketDetailPage.Visibility = Visibility.Collapsed;
        MarketPrimaryButton.Visibility = MarketCancelButton.Visibility = Visibility.Collapsed;
        MarketNavigationHint.Text = Loc.T("Esc Back   ↑↓ Navigate   Enter Open");
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
            _restarting = true; _error = null; UpdateDetail();
            try { _error = await RestartRequested(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _error = Loc.T("Restart could not finish. Close and reopen TypeWhisper to apply the update."); }
            finally { _restarting = false; if (IsDetail) UpdateDetail(); }
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
        _installation = operation; _error = null; _operationMessage = Loc.T("Preparing installation…"); SetProgress(0);
        UpdateDetail();
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
            // A new install starts enabled so its settings page can show what it provides.
            // If enabling is refused (e.g. while recording), the page still offers Enable.
            if (openSettings && _runtime.GetPluginBinding(item.Plugin.Id) is { } binding && !binding.IsEnabled())
            {
                _operationMessage = Loc.T("Enabling {0}…", item.Plugin.Title);
                SetProgress(null);
                if (IsDetail) MarketStatusExplanation.Text = _operationMessage;
                if (await binding.ChangeEnabledAsync(true) is not null)
                    AppDiagnostics.Write($"plugin.install.enable-failed plugin={item.Plugin.Id}");
                // Enabling cannot be interrupted, but Cancel still keeps the user on this page.
                openSettings = !operation.IsCancellationRequested;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _error = ex.Message; }
        finally
        {
            if (ReferenceEquals(_installation, operation))
            {
                _installation = null;
                if (openSettings && IsPageVisible())
                {
                    ShowList(true);
                    ManageRequested?.Invoke(item.Plugin.Id);
                }
                else if (IsDetail) { UpdateDetail(); if (IsPageVisible()) MarketPrimaryButton.Focus(FocusState.Programmatic); }
                else Filter(_query);
            }
        }
    }

    private void UpdateCategories()
    {
        var ids = _catalog.SelectMany(item => item.CategoriesIds).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (!ids.Contains(_categoriesFilter, StringComparer.OrdinalIgnoreCase)) _categoriesFilter = "";
        CatalogFilters.SetItems(new[] { new Tab("all", Loc.T("All")) }.Concat(ids.Select(id =>
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
