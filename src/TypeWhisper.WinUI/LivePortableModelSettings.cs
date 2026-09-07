using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal sealed class LivePortableModelSettings : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly string _pluginId;
    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly TextBlock _status = Label("");
    private readonly TextBlock _llm = Label("");
    private readonly HandCursorButton _refresh;
    private readonly Dictionary<(string Provider, string Model), Row> _items = [];
    private CancellationTokenSource? _lifetime;
    private bool _reading;
    private bool _working;
    private bool _canceling;
    private int _queued;
    private bool _reloadRequested;

    internal LivePortableModelSettings(LocalDictationSession session, string pluginId)
    {
        _session = session; _pluginId = pluginId;
        _refresh = Button("Refresh models");
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(_refresh); content.Children.Add(_status); content.Children.Add(_rows); content.Children.Add(_llm);
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
            var llms = _session.LlmProviders.Where(p => p.PluginId == _pluginId).ToArray();
            _llm.Text = models.Count == 0 && llms.Length == 0 ? "No model providers are currently enabled." :
                string.Join("\n", llms.Select(p => p.Name + " · Text processing: " + string.Join(", ", p.Models.Select(m => m.DisplayName))));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = "Model status could not be read. Refresh after the current plugin operation finishes."; }
        finally
        {
            _reading = false;
            if (Current(lifetime)) UpdateButtons();
            if (IsLoaded && _reloadRequested) QueueUpdate();
        }
    }

    private void SetRow(PortableDownloadableModel model)
    {
        var key = (model.SelectionId, model.ModelId);
        if (!_items.TryGetValue(key, out var row))
        {
            row = new Row(model);
            _items.Add(key, row); _rows.Children.Add(row.Panel);
            var captured = row;
            row.Download.Click += async (_, _) => await DownloadAsync(captured);
            row.Use.Click += async (_, _) => await UseAsync(captured);
            row.Remove.Click += async (_, _) => await RemoveAsync(captured);
            row.Cancel.Click += async (_, _) => await CancelAsync(captured);
        }
        row.Model = model;
        row.Title.Text = model.DisplayName + (string.IsNullOrWhiteSpace(model.SizeDescription) ? "" : " · " + model.SizeDescription);
        row.Requirements.Text = string.Join("\n", model.Requirements.Select(r =>
            $"{r.Title} · {(r.IsRequired ? "Required" : "Optional")} · {(r.IsSatisfied ? "Satisfied" : "Not satisfied")}\n{r.Description}"));
        row.Requirements.Visibility = model.Requirements.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        row.RemovalNote.Text = model.RemovalBlockedReason ?? "";
        row.RemovalNote.Visibility = model.SupportsRemoval && model.Downloaded && model.RemovalBlockedReason is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Current(CancellationTokenSource lifetime) => IsLoaded && ReferenceEquals(_lifetime, lifetime) && !lifetime.IsCancellationRequested;
    private static bool SameOwner(PortableDownloadableModel left, PortableDownloadableModel right) =>
        left.PluginId == right.PluginId && left.SelectionId == right.SelectionId && left.ModelId == right.ModelId &&
        left.Generation == right.Generation && left.Version == right.Version && left.EngineIdentity == right.EngineIdentity;

    private async Task UseAsync(Row row)
    {
        if (_working || _reading || _lifetime is not { } lifetime || !row.Use.IsEnabled) return;
        var expected = row.Model;
        _working = true; UpdateButtons();
        try
        {
            if (!Current(lifetime)) return;
            var error = await _session.UseRegistryModelAsync(expected);
            if (Current(lifetime)) _status.Text = error ?? "Model selected for dictation.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = "The model could not be selected. Refresh its status and try again."; }
        finally { _working = false; if (IsLoaded) RequestRefresh(); }
    }

    private async Task DownloadAsync(Row row)
    {
        if (_working || _reading || _lifetime is not { } lifetime || !row.Download.IsEnabled) return;
        _working = true; UpdateButtons();
        try
        {
            var error = await _session.DownloadRegistryModelAsync(row.Model);
            if (Current(lifetime)) _status.Text = error ?? "Model downloaded. Choose Use model to select it.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = "The download could not finish. Refresh the model status before retrying."; }
        finally { _working = false; if (IsLoaded) RequestRefresh(); }
    }

    private async Task CancelAsync(Row row)
    {
        if (_canceling || _lifetime is not { } lifetime || !row.Cancel.IsEnabled || !IsActive(row.Model)) return;
        _canceling = true;
        row.Cancel.IsEnabled = false;
        try
        {
            await _session.CancelRegistryModelDownloadAsync();
            if (Current(lifetime)) _status.Text = _session.RegistryModelDownload.State.Message ?? "Model operation stopped.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = "The model operation could not finish stopping. Wait before retrying."; }
        finally { _canceling = false; if (IsLoaded) RequestRefresh(); }
    }

    private async Task RemoveAsync(Row row)
    {
        if (_working || _reading || _lifetime is not { } lifetime || !row.Remove.IsEnabled) return;
        var expected = row.Model;
        _working = true; UpdateButtons();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = "Remove " + expected.DisplayName + "?",
                Content = "Downloaded files for this model will be removed. You will need to download it again before using it. The plugin and its settings will be kept.",
                PrimaryButtonText = "Remove model",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !Current(lifetime)) return;
            var error = await _session.RemoveRegistryModelAsync(expected);
            if (Current(lifetime)) _status.Text = error ?? "Model removed. Download it again to use it.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = "The model could not be removed. Refresh its status before trying again."; }
        finally { _working = false; if (IsLoaded) RequestRefresh(); }
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
            var model = row.Model;
            var provider = _session.PluginRuntime.TranscriptionProviders.FirstOrDefault(p => p.PluginId == _pluginId && p.SelectionId == model.SelectionId);
            var active = IsActive(model);
            var selected = _session.IsRegistryModelSelected(model) && provider?.Ready == true;
            var required = model.Requirements.Any(r => r.IsRequired && !r.IsSatisfied);
            row.Download.Visibility = model.SupportsDownload && !model.Downloaded ? Visibility.Visible : Visibility.Collapsed;
            row.Download.IsEnabled = available && provider is not null && !required;
            row.Use.Content = selected ? "Active model" : "Use model";
            row.Use.IsEnabled = available && provider is not null && !selected &&
                (model.SupportsDownload ? model.Downloaded : provider.Ready);
            row.Remove.Visibility = model.SupportsRemoval && model.Downloaded ? Visibility.Visible : Visibility.Collapsed;
            row.Remove.IsEnabled = available && provider is not null && !selected && model.RemovalBlockedReason is null;
            ToolTipService.SetToolTip(row.Remove, model.RemovalBlockedReason ?? "Remove downloaded files for this model.");
            AutomationProperties.SetHelpText(row.Remove, model.RemovalBlockedReason ?? "Remove downloaded files for this model.");
            row.Cancel.Visibility = active && state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            row.Cancel.Content = state.IsRemoval ? "Cancel removal" : "Cancel download";
            row.Cancel.IsEnabled = !_canceling && active && state.IsBusy && !state.IsClosing;
            row.Progress.Visibility = active && state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            row.Progress.IsIndeterminate = state.Progress is null;
            row.Progress.Value = (state.Progress ?? 0) * 100;
            row.State.Text = active && state.Message is not null ? state.Message : provider is null
                ? "Provider unavailable. Refresh after enabling the plugin." : model.SupportsDownload
                    ? selected ? "Loaded · active for dictation." : model.Downloaded ? "Downloaded · choose Use model to load it." : "Not downloaded."
                    : provider.Ready ? "Provider ready." : "Complete provider configuration before selecting a model.";
        }
    }

    private sealed class Row
    {
        internal PortableDownloadableModel Model;
        internal readonly StackPanel Panel = new() { Spacing = 6 };
        internal readonly TextBlock Title = Label("");
        internal readonly TextBlock Requirements = Label("");
        internal readonly TextBlock State = Label("");
        internal readonly TextBlock RemovalNote = Label("");
        internal readonly ProgressBar Progress = new() { Minimum = 0, Maximum = 100 };
        internal readonly HandCursorButton Download = Button("Download model");
        internal readonly HandCursorButton Use = Button("Use model");
        internal readonly HandCursorButton Remove = Button("Remove model");
        internal readonly HandCursorButton Cancel = Button("Cancel operation");
        internal Row(PortableDownloadableModel model)
        {
            Model = model;
            var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            Panel.SizeChanged += (_, e) => actions.Orientation = e.NewSize.Width < 440 ? Orientation.Vertical : Orientation.Horizontal;
            actions.Children.Add(Download); actions.Children.Add(Use); actions.Children.Add(Remove); actions.Children.Add(Cancel);
            Panel.Children.Add(Title); Panel.Children.Add(Requirements); Panel.Children.Add(State); Panel.Children.Add(RemovalNote); Panel.Children.Add(Progress); Panel.Children.Add(actions);
            AutomationProperties.SetName(Progress, model.DisplayName + " model operation progress");
            AutomationProperties.SetName(Remove, "Remove " + model.DisplayName);
        }
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private static HandCursorButton Button(string text) => new() { Content = text,
        Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
}
