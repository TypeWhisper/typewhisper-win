using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

internal sealed class LivePortableModelSettings : UserControl
{
    private readonly LiveLocalTtsModelSettings _localTts;
    internal event Action? ConfigurationChanged;
    private readonly LocalDictationSession _session;
    private readonly string _pluginId;
    private readonly StackPanel _rows = new();
    private readonly Border _modelCard;
    private readonly TextBlock _status = Label("");
    private readonly TextBlock _llm = Label("");
    private readonly HandCursorButton _refresh;
    private readonly StackPanel _cloudPanel = new() { Spacing = 8 };
    private readonly ComboBox _cloudModel = new() { DisplayMemberPath = nameof(PortableDownloadableModel.DisplayName), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
    private readonly TextBlock _cloudStatus = Label("");
    private readonly HandCursorButton _cloudUse = Button(Loc.T("Use selected model"));
    private readonly LiveLocalLlmModelSettings _localLlm;
    internal bool HasLocalLlmModels { get; set; }
    private bool _cloudMode;
    private bool _settingCloudModel;
    internal bool HasLocalTtsModels { get; set; }
    internal bool ShowLlmSummary { get; set; } = true;
    internal IReadOnlyList<HashSet<string>> TranscriptionModelSettingChoices { get; set; } = [];
    private readonly Dictionary<(string Provider, string Model), Row> _items = [];
    private CancellationTokenSource? _lifetime;
    private bool _reading;
    private bool _working;
    private Row? _loadingRow;
    private bool _canceling;
    private int _queued;
    private bool _reloadRequested;

    internal LivePortableModelSettings(LocalDictationSession session, string pluginId)
    {
        _session = session; _pluginId = pluginId;
        _refresh = Button(Loc.T("Refresh models"));
        _status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => _status.Visibility = string.IsNullOrWhiteSpace(_status.Text) ? Visibility.Collapsed : Visibility.Visible);
        _status.Visibility = Visibility.Collapsed;
        var content = new StackPanel { Spacing = 10 };
        _cloudPanel.Children.Add(new TextBlock { Text = Loc.T("Transcription model"), FontSize = 16 });
        _cloudPanel.Children.Add(_cloudModel); _cloudPanel.Children.Add(_cloudStatus); _cloudPanel.Children.Add(_cloudUse);
        _cloudPanel.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_cloudModel, Loc.T("Transcription model"));
        _cloudUse.Click += async (_, _) =>
        { if (_cloudModel.SelectedItem is PortableDownloadableModel model && _items.TryGetValue((model.SelectionId, model.ModelId), out var row)) await UseAsync(row); };
        _cloudModel.SelectionChanged += async (_, _) =>
        {
            if (_settingCloudModel || !_cloudModel.IsLoaded || _cloudModel.SelectedItem is not PortableDownloadableModel model) return;
            if (_items.TryGetValue((model.SelectionId, model.ModelId), out var row)) await UseAsync(row);
        };
        _localTts = new(session, pluginId);
        content.Children.Add(_localTts);
        _localLlm = new(session, pluginId) { Visibility = Visibility.Collapsed };
        content.Children.Add(_localLlm);
        // One card for all models, as on the NVIDIA Parakeet page; Refresh sits in its heading.
        var heading = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 12) };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = Loc.T("Models"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_refresh, 1); heading.Children.Add(_refresh);
        var list = new StackPanel();
        list.Children.Add(heading); list.Children.Add(_rows);
        _modelCard = new Border { Child = list, Padding = new Thickness(18, 12, 18, 2), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        void Theme()
        {
            _modelCard.Background = (Brush)Application.Current.Resources["SurfaceBrush"];
            _modelCard.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
        }
        _modelCard.ActualThemeChanged += (_, _) => Theme(); Theme();
        content.Children.Add(_status); content.Children.Add(_cloudPanel); content.Children.Add(_modelCard); content.Children.Add(_llm);
        Content = content;
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _refresh.Click += (_, _) => RequestRefresh();
        Loaded += (_, _) =>
        {
            _lifetime = new();
            session.Changed += Changed;
            session.RegistryModelDownload.Changed += Changed;
            RequestRefresh();
        };
        Unloaded += (_, _) =>
        {
            foreach (var row in _items.Values) foreach (var credential in row.Credentials.Values) credential.Input.Password = "";
            session.Changed -= Changed;
            session.RegistryModelDownload.Changed -= Changed;
            try { _lifetime?.Cancel(); }
            catch (AggregateException) { /* The canceled view cannot admit another read or command. */ }
            _lifetime?.Dispose();
            _lifetime = null;
        };
    }

    // Provider state reads acquire a lease and notify Session. Never call them from Changed.
    internal void RequestRefresh()
    {
        _reloadRequested = true;
        QueueUpdate();
    }

    private void Changed() => QueueUpdate();
    private void QueueUpdate()
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (!IsLoaded) return;
            UpdateButtons();
            if (_reloadRequested && !_reading && !_working && !_session.RegistryModelDownload.State.IsBusy)
            {
                _reloadRequested = false;
                await ReadModelsAsync();
            }
        })) Interlocked.Exchange(ref _queued, 0);
    }

    private async Task ReadModelsAsync()
    {
        if (_lifetime is not { } lifetime) return;
        var token = lifetime.Token;
        _reading = true; UpdateButtons();
        try
        {
            var localTts = await _session.PluginRuntime.UseConfigurationAsync(_pluginId,
                (plugin, _) => Task.FromResult(plugin is ILocalTtsModelManagement), token);
            if (!Current(lifetime)) return;
            _localTts.Visibility = localTts ? Visibility.Visible : Visibility.Collapsed;
            var models = new List<PortableDownloadableModel>();
            foreach (var provider in _session.PluginRuntime.TranscriptionProviders.Where(p => p.PluginId == _pluginId).ToArray())
                models.AddRange(await _session.PluginRuntime.GetModelStatesAsync(provider.SelectionId, token));
            if (!Current(lifetime)) return;
            var keys = models.Select(m => (m.SelectionId, m.ModelId)).ToHashSet();
            if (_session.RegistryModelDownload.State.IsBusy && _session.ActiveRegistryModelDownload is { } active && active.PluginId == _pluginId)
                keys.Add((active.SelectionId, active.ModelId));
            foreach (var key in _items.Keys.Where(key => !keys.Contains(key)).ToArray())
            { _rows.Children.Remove(_items[key].Panel); _items.Remove(key); }
            foreach (var model in models) SetRow(model);
            for (var index = 0; index < models.Count; index++)
            {
                var model = models[index];
                var panel = _items[(model.SelectionId, model.ModelId)].Panel;
                if (_rows.Children.IndexOf(panel) == index) continue;
                _rows.Children.Remove(panel);
                _rows.Children.Insert(index, panel);
            }
            _cloudMode = models.Count > 0 && models.All(m => !m.SupportsDownload && !m.SupportsRemoval)
                && models.Select(m => m.SelectionId).Distinct().Count() == 1;
            var hasModelSetting = _cloudMode && TranscriptionModelSettingChoices.Any(choices =>
                choices.SetEquals(models.Select(model => model.ModelId)));
            _cloudPanel.Visibility = _cloudMode && !hasModelSetting ? Visibility.Visible : Visibility.Collapsed;
            _refresh.Visibility = _cloudMode || !_session.PluginRuntime.TranscriptionProviders.Any(p => p.PluginId == _pluginId)
                ? Visibility.Collapsed : Visibility.Visible;
            _localLlm.Visibility = HasLocalLlmModels ? Visibility.Visible : Visibility.Collapsed;
            _settingCloudModel = true;
            try
            {
                _cloudModel.ItemsSource = _cloudMode ? models : [];
                var selectedId = _session.PluginRuntime.TranscriptionProviders.FirstOrDefault(p => p.PluginId == _pluginId)?.SelectedModelId;
                _cloudModel.SelectedItem = models.FirstOrDefault(m => m.ModelId == selectedId);
            }
            finally { _settingCloudModel = false; }
            _llm.Visibility = ShowLlmSummary && !HasLocalLlmModels ? Visibility.Visible : Visibility.Collapsed;
            var llms = _session.LlmProviders.Where(p => p.PluginId == _pluginId).ToArray();
            _llm.Text = models.Count == 0 && llms.Length == 0 && !localTts && !HasLocalLlmModels ? Loc.T("No model providers are currently enabled.") :
                string.Join("\n", llms.Select(p => Loc.T("{0} · Text processing: {1}", p.Name, string.Join(", ", p.Models.Select(m => m.DisplayName)))));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("Model status could not be read. Refresh after the current plugin operation finishes."); }
        finally
        {
            _reading = false;
            if (Current(lifetime)) { UpdateButtons(); UpdateModelCard(); }
            if (IsLoaded && _reloadRequested) QueueUpdate();
        }
    }

    // Refresh sits in the card, so the card stays while a provider has no rows yet or a read failed.
    private void UpdateModelCard() => _modelCard.Visibility = !_cloudMode && (_rows.Children.Count > 0
        || _session.PluginRuntime.TranscriptionProviders.Any(p => p.PluginId == _pluginId)) ? Visibility.Visible : Visibility.Collapsed;

    private void SetRow(PortableDownloadableModel model)
    {
        var key = (model.SelectionId, model.ModelId);
        if (!_items.TryGetValue(key, out var row))
        {
            row = new Row(model);
            _items.Add(key, row); _rows.Children.Add(row.Panel);
            UpdateModelCard();
            var captured = row;
            row.Download.Click += async (_, _) => await DownloadAsync(captured);
            row.Use.Click += async (_, _) => await UseAsync(captured);
            row.Remove.Click += async (_, _) => await RemoveAsync(captured);
            row.Cancel.Click += async (_, _) => await CancelAsync(captured);
        }
        if (!SameOwner(row.Model, model)) foreach (var credential in row.Credentials.Values) credential.Input.Password = "";
        row.Model = model;
        var requirements = model.Requirements.Where(r => r.Kind == PluginModelDownloadRequirementKind.Credential).ToArray();
        foreach (var old in row.Credentials.Keys.Where(id => !requirements.Any(r => r.Id == id)).ToArray())
        { row.CredentialPanel.Children.Remove(row.Credentials[old].Panel); row.Credentials.Remove(old); }
        foreach (var requirement in requirements)
        {
            if (!row.Credentials.TryGetValue(requirement.Id, out var credential))
            {
                credential = new CredentialRow(requirement.IsRequired ? requirement.Title : Loc.T("{0} (optional)", requirement.Title));
                row.Credentials.Add(requirement.Id, credential); row.CredentialPanel.Children.Add(credential.Panel);
                var capturedRow = row; var capturedCredential = credential; var id = requirement.Id;
                credential.Save.Click += async (_, _) => await SaveCredentialAsync(capturedRow, id, capturedCredential, clear: false);
                credential.Clear.Click += async (_, _) => await SaveCredentialAsync(capturedRow, id, capturedCredential, clear: true);
            }
            credential.Input.PlaceholderText = requirement.IsSatisfied ? Loc.T("Saved securely; enter a replacement") : Loc.T("Enter download token");
            credential.Clear.Visibility = requirement.IsSatisfied ? Visibility.Visible : Visibility.Collapsed;
        }
        row.Title.Text = model.DisplayName;
        row.Size.Text = model.SizeDescription ?? Loc.T("Local model");
        row.Requirements.Text = string.Join("\n", model.Requirements.Where(r => r.IsRequired && !r.IsSatisfied).Select(r =>
            Loc.T("{0} required · {1}", r.Title, r.Description)));
        row.Requirements.Visibility = string.IsNullOrEmpty(row.Requirements.Text) ? Visibility.Collapsed : Visibility.Visible;
        row.RemovalNote.Text = model.RemovalBlockedReason ?? "";
        row.RemovalNote.Visibility = model.SupportsRemoval && model.Downloaded && model.RemovalBlockedReason is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Current(CancellationTokenSource lifetime) => IsLoaded && ReferenceEquals(_lifetime, lifetime) && !lifetime.IsCancellationRequested;
    private static bool SameOwner(PortableDownloadableModel left, PortableDownloadableModel right) =>
        left.PluginId == right.PluginId && left.SelectionId == right.SelectionId && left.ModelId == right.ModelId &&
        left.Generation == right.Generation && left.Version == right.Version && left.EngineIdentity == right.EngineIdentity;

    private async Task SaveCredentialAsync(Row row, string id, CredentialRow credential, bool clear)
    {
        if (_working || _reading || _lifetime is not { } lifetime || !_session.CanStartPluginSettingsAction
            || _session.RegistryModelDownload.State.IsBusy) return;
        var expected = row.Model;
        var value = clear ? null : credential.Input.Password;
        _working = true; UpdateButtons();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        void CancelForRecording() => cancellation.Cancel();
        _session.RecordingStarting += CancelForRecording;
        try
        {
            var result = await _session.PluginRuntime.UpdateModelDownloadCredentialAsync(expected, id, value, cancellation.Token);
            if (Current(lifetime))
            {
                if (result.Succeeded) credential.Input.Password = "";
                _status.Text = result.Message ?? (result.Succeeded ? Loc.T("Download credential saved.") : Loc.T("The credential could not be saved."));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("The download credential could not be updated. Refresh and try again."); }
        finally { _session.RecordingStarting -= CancelForRecording; _working = false; if (IsLoaded) RequestRefresh(); }
    }

    private async Task UseAsync(Row row)
    {
        if (_working || _reading || _lifetime is not { } lifetime || !row.Use.IsEnabled) return;
        var expected = row.Model;
        _loadingRow = row;
        _working = true; UpdateButtons();
        try
        {
            if (!Current(lifetime)) return;
            var error = await _session.UseRegistryModelAsync(expected);
            if (Current(lifetime)) _status.Text = error ?? Loc.T("Model selected for dictation.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("The model could not be selected. Refresh its status and try again."); }
        finally { _loadingRow = null; _working = false; if (IsLoaded) { RequestRefresh(); ConfigurationChanged?.Invoke(); } }
    }

    private async Task DownloadAsync(Row row)
    {
        if (_working || _reading || _lifetime is not { } lifetime || !row.Download.IsEnabled) return;
        _working = true; UpdateButtons();
        try
        {
            var error = await _session.DownloadRegistryModelAsync(row.Model);
            if (Current(lifetime)) _status.Text = error ?? Loc.T("Model downloaded. Choose Use model to select it.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("The download could not finish. Refresh the model status before retrying."); }
        finally { _working = false; if (IsLoaded) { RequestRefresh(); ConfigurationChanged?.Invoke(); } }
    }

    private async Task CancelAsync(Row row)
    {
        if (_canceling || _lifetime is not { } lifetime || !row.Cancel.IsEnabled || !IsActive(row.Model)) return;
        _canceling = true;
        row.Cancel.IsEnabled = false;
        try
        {
            await _session.CancelRegistryModelDownloadAsync();
            if (Current(lifetime)) _status.Text = _session.RegistryModelDownload.State.Message ?? Loc.T("Model operation stopped.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("The model operation could not finish stopping. Wait before retrying."); }
        finally { _canceling = false; if (IsLoaded) { RequestRefresh(); ConfigurationChanged?.Invoke(); } }
    }

    private async Task RemoveAsync(Row row)
    {
        if (_working || _reading || _lifetime is not { } lifetime || !row.Remove.IsEnabled) return;
        var expected = row.Model;
        _working = true; UpdateButtons();
        try
        {
            if (!await Dialogs.ConfirmAsync(this, Loc.T("Remove {0}?", expected.DisplayName),
                Loc.T("Downloaded files for this model will be removed. You will need to download it again before using it. The plugin and its settings will be kept."), Loc.T("Remove model"), destructive: true) || !Current(lifetime)) return;
            var error = await _session.RemoveRegistryModelAsync(expected);
            if (Current(lifetime)) _status.Text = error ?? Loc.T("Model removed. Download it again to use it.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("The model could not be removed. Refresh its status before trying again."); }
        finally { _working = false; if (IsLoaded) { RequestRefresh(); ConfigurationChanged?.Invoke(); } }
    }

    private bool IsActive(PortableDownloadableModel model) => _session.ActiveRegistryModelDownload is { } active && SameOwner(active, model);
    private void UpdateButtons()
    {
        var state = _session.RegistryModelDownload.State;
        // The download owns the provider lease. Its detached snapshot must provide
        // progress and Cancel even before a fresh view can read the model registry.
        if (state.IsBusy && _session.ActiveRegistryModelDownload is { } activeModel && activeModel.PluginId == _pluginId)
            SetRow(activeModel);
        var available = !_working && !_reading && !state.IsBusy && !state.IsClosing && _session.CanChangeProvider;
        _refresh.IsEnabled = available;
        foreach (var row in _items.Values)
        {
            row.CredentialPanel.IsHitTestVisible = available;
            foreach (var credential in row.Credentials.Values) foreach (var control in credential.Panel.Children.OfType<Control>()) control.IsEnabled = available;
            var model = row.Model;
            var provider = _session.PluginRuntime.TranscriptionProviders.FirstOrDefault(p => p.PluginId == _pluginId && p.SelectionId == model.SelectionId);
            var active = IsActive(model);
            var selected = _session.IsRegistryModelSelected(model) && provider?.Ready == true;
            var required = model.Requirements.Any(r => r.IsRequired && !r.IsSatisfied);
            row.Download.Visibility = model.SupportsDownload && !model.Downloaded && !(active && state.IsBusy) ? Visibility.Visible : Visibility.Collapsed;
            row.Download.IsEnabled = available && provider is not null && !required;
            // The badge marks the selected model, so its button is not shown.
            row.Use.Visibility = selected || model.SupportsDownload && !model.Downloaded ? Visibility.Collapsed : Visibility.Visible;
            var loading = ReferenceEquals(_loadingRow, row);
            var recommended = provider?.Models.FirstOrDefault(m => m.Id == model.ModelId)?.IsRecommended == true;
            row.SetBadge(selected ? Loc.T("Active") : recommended && !model.Downloaded ? Loc.T("Recommended") : "");
            row.Use.IsEnabled = available && provider is not null && !selected &&
                (model.SupportsDownload ? model.Downloaded : provider.Ready);
            row.Remove.Visibility = model.SupportsRemoval && model.Downloaded ? Visibility.Visible : Visibility.Collapsed;
            row.Remove.IsEnabled = available && provider is not null && !selected && model.RemovalBlockedReason is null;
            ToolTipService.SetToolTip(row.Remove, model.RemovalBlockedReason ?? Loc.T("Remove downloaded files for this model."));
            AutomationProperties.SetHelpText(row.Remove, model.RemovalBlockedReason ?? Loc.T("Remove downloaded files for this model."));
            row.Cancel.Visibility = active && state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            row.Cancel.Content = state.IsRemoval ? Loc.T("Cancel removal") : Loc.T("Cancel download");
            row.Cancel.IsEnabled = !_canceling && active && state.IsBusy && !state.IsClosing;
            row.Progress.Visibility = loading || active && state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            row.Percent.Text = active && state.IsBusy && state.Progress is { } fraction ? $"{fraction:P0}" : "";
            row.Percent.Visibility = row.ProgressLine.Visibility = row.Progress.Visibility;
            row.Progress.IsIndeterminate = loading || state.Progress is null;
            row.Progress.Value = (state.Progress ?? 0) * 100;
            row.State.Text = loading ? Loc.T("Loading model into memory…") : active && state.Message is not null ? state.Message : provider is null
                ? Loc.T("Provider unavailable. Refresh after enabling the plugin.") : model.SupportsDownload
                    ? selected ? Loc.T("Downloaded · 100%. Selected for dictation.") : model.Downloaded ? Loc.T("Downloaded · 100%. Choose Use model to load it.") : Loc.T("Not downloaded.")
                    : provider.Ready ? Loc.T("Provider ready.") : Loc.T("Complete provider configuration before selecting a model.");
            // Shown only when it says more than the badge and buttons do.
            row.State.Visibility = loading || active && state.Message is not null || provider is null || !model.SupportsDownload && !provider.Ready
                ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_cloudMode)
        {
            var currentModel = _cloudModel.SelectedItem as PortableDownloadableModel;
            var selectionId = currentModel?.SelectionId ?? _items.Values.FirstOrDefault()?.Model.SelectionId;
            var currentProvider = _session.PluginRuntime.TranscriptionProviders.FirstOrDefault(p => p.SelectionId == selectionId);
            _cloudModel.IsEnabled = available && currentProvider?.Ready == true;
            _cloudUse.IsEnabled = false;
            _cloudUse.Visibility = Visibility.Collapsed;
            _cloudStatus.Text = currentProvider?.Ready == true ? Loc.T("Choose a transcription model.") : Loc.T("Complete provider configuration before selecting a model.");
        }
        if (_cloudMode && _cloudModel.SelectedItem is PortableDownloadableModel selectedModel && _items.TryGetValue((selectedModel.SelectionId, selectedModel.ModelId), out var selectedRow))
        {
            var provider = _session.PluginRuntime.TranscriptionProviders.FirstOrDefault(p => p.SelectionId == selectedModel.SelectionId);
            _cloudModel.IsEnabled = available && provider?.Ready == true;
            _cloudUse.IsEnabled = selectedRow.Use.IsEnabled;
            _cloudUse.Visibility = _session.IsRegistryModelSelected(selectedModel) ? Visibility.Collapsed : Visibility.Visible;
            _cloudStatus.Text = provider?.Ready == true
                ? _session.IsRegistryModelSelected(selectedModel) ? Loc.T("Selected for dictation.") : Loc.T("Choose this model to use it for dictation.")
                : Loc.T("An API key is required for transcription.");
        }
    }

    private sealed class CredentialRow
    {
        internal readonly StackPanel Panel = new() { Spacing = 6 };
        internal readonly PasswordBox Input = new();
        internal readonly HandCursorButton Save = Button(Loc.T("Save token"));
        internal readonly HandCursorButton Clear = Button(Loc.T("Remove saved token"));
        internal CredentialRow(string title)
        {
            AutomationProperties.SetName(Input, title);
            Panel.Children.Add(Label(title)); Panel.Children.Add(Input); Panel.Children.Add(Save); Panel.Children.Add(Clear);
        }
    }

    private sealed class Row
    {
        internal PortableDownloadableModel Model;
        // A hairline separates each row from the heading or the row above.
        internal readonly Border Panel = new() { Padding = new Thickness(0, 14, 0, 14), BorderThickness = new Thickness(0, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        internal readonly StackPanel CredentialPanel = new() { Spacing = 8 };
        internal readonly Dictionary<string, CredentialRow> Credentials = [];
        internal readonly TextBlock Title = Label("");
        internal readonly TextBlock Size = Label("");
        internal readonly TextBlock Badge = Label("");
        internal readonly TextBlock Percent = Label("");
        internal readonly TextBlock Requirements = Label("");
        internal readonly TextBlock State = Label("");
        internal readonly TextBlock RemovalNote = Label("");
        internal readonly ProgressBar Progress = new() { Minimum = 0, Maximum = 100, Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        internal readonly Grid ProgressLine = new() { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        internal readonly HandCursorButton Download = Button(Loc.T("Download model"));
        internal readonly HandCursorButton Use = Button(Loc.T("Use model"));
        internal readonly HandCursorButton Remove = Button(Loc.T("Remove"));
        internal readonly HandCursorButton Cancel = Button(Loc.T("Cancel operation"));
        private readonly Border _badge;
        // An empty badge is hidden; the buttons already say whether a model is downloaded.
        internal void SetBadge(string text)
        {
            Badge.Text = text;
            _badge.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        internal Row(PortableDownloadableModel model)
        {
            Model = model;
            Download.Style = Use.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
            Title.FontSize = 15; Title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            Badge.FontSize = 11; Badge.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var layout = new Grid { ColumnSpacing = 14 };
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            _badge = new Border { Child = Badge, Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            // Left-aligned, the star column is as wide as the name but still wraps a long one.
            var title = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            title.Children.Add(Title); Grid.SetColumn(_badge, 1); title.Children.Add(_badge);
            // Collapsed with its bar, so an idle row keeps no gap for it.
            var progressLine = ProgressLine;
            progressLine.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            progressLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            progressLine.Children.Add(Progress); Grid.SetColumn(Percent, 1); progressLine.Children.Add(Percent);
            copy.Children.Add(title); copy.Children.Add(Size); copy.Children.Add(Requirements); copy.Children.Add(CredentialPanel);
            copy.Children.Add(State); copy.Children.Add(RemovalNote); copy.Children.Add(progressLine);
            layout.Children.Add(copy);
            var actions = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Download); actions.Children.Add(Use); actions.Children.Add(Cancel); actions.Children.Add(Remove);
            Grid.SetColumn(actions, 1); layout.Children.Add(actions);
            Panel.Child = layout;
            Panel.SizeChanged += (_, e) =>
            {
                var narrow = e.NewSize.Width < 480;
                Grid.SetColumnSpan(copy, narrow ? 2 : 1);
                Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
                actions.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
                // Three buttons can show at once; stacked, none is cut off in a narrow window.
                actions.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
                actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            };
            void Theme() {
                Panel.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
                Size.Foreground = State.Foreground = RemovalNote.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
                _badge.Background = (Brush)Application.Current.Resources["ElevatedBrush"];
                Badge.Foreground = Progress.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
            }
            Panel.ActualThemeChanged += (_, _) => Theme(); Theme();
            AutomationProperties.SetName(Progress, Loc.T("{0} model operation progress", model.DisplayName));
            AutomationProperties.SetName(Remove, Loc.T("Remove {0}", model.DisplayName));
        }
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private static HandCursorButton Button(string text) => new() { Content = text,
        Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
}
