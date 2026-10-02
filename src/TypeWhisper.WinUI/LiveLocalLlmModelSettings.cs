using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

// Local text models deliberately do not enter the dictation-provider registry.
internal sealed class LiveLocalLlmModelSettings : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly string _pluginId;
    private readonly StackPanel _content = new() { Spacing = 12 };
    private readonly TextBlock _status = Label("", true);
    private readonly List<Row> _rows = [];
    private readonly ProgressBar _downloadProgress = new() { Minimum = 0, Maximum = 100, Height = 6 };
    private readonly HandCursorButton _cancelDownload = Button(Loc.T("Cancel download"));
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _refreshPending;

    internal LiveLocalLlmModelSettings(LocalDictationSession session, string pluginId)
    {
        _session = session; _pluginId = pluginId;
        Content = _content;
        _content.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _cancelDownload.Click += async (_, _) =>
        {
            if (_session.LocalLlmDownloadPluginId == _pluginId)
                await _session.LocalLlmDownload.CancelAndDrainAsync();
        };
        Loaded += async (_, _) =>
        {
            _lifetime = new();
            _session.LocalLlmDownload.Changed += DownloadChanged;
            _session.PluginRuntime.Changed += ModelStateChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _session.LocalLlmDownload.Changed -= DownloadChanged;
            _session.PluginRuntime.Changed -= ModelStateChanged;
            _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        };
    }

    private async Task RefreshAsync()
    {
        if (_lifetime is not { } lifetime || _busy) return;
        if (ShowActiveDownload()) return;
        _busy = true;
        try
        {
            var models = await _session.PluginRuntime.UseConfigurationAsync(_pluginId, (plugin, _) =>
                Task.FromResult((plugin as ILocalLlmModelManagement)?.LocalModels.ToArray() ?? []), lifetime.Token,
                refreshCapabilities: false);
            if (!Current(lifetime)) return;
            var restorable = _session.PluginRuntime.RestorableLocalLlmModel(_pluginId);
            _rows.Clear(); _content.Children.Clear();
            if (models.Length == 0)
            {
                _status.Text = Loc.T("No local text models are currently available. Re-enable the plugin or reopen this page to retry.");
                _content.Children.Add(_status);
                return;
            }
            // One card for all models, as on the NVIDIA Parakeet page.
            var list = new StackPanel();
            var heading = Label(Loc.T("Models")); heading.FontSize = 13; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            list.Children.Add(heading);
            var intro = Label(Loc.T("Local text processing · CPU\nDownload a model, then load it to use it in a workflow. Your text stays on this device. An idle model is released as set under Unload idle models in Advanced settings and loads again when needed."), true);
            intro.Margin = new Thickness(0, 4, 0, 14);
            list.Children.Add(intro);
            _content.Children.Add(new Border { Child = list, Padding = new Thickness(18, 16, 18, 2), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                Background = (Brush)Application.Current.Resources["SurfaceBrush"], BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"] });
            _content.Children.Add(_status);
            foreach (var model in models)
            {
                var row = new Row(model, !model.Loaded && restorable == model.Model.Id);
                _rows.Add(row); list.Children.Add(row.Panel);
                row.Download.Click += async (_, _) => await RunAsync(row, "download");
                row.Load.Click += async (_, _) => await RunAsync(row, "load");
                row.Unload.Click += async (_, _) => await RunAsync(row, "unload");
                row.Remove.Click += async (_, _) => await RunAsync(row, "remove");
                row.Cancel.Click += (_, _) => { row.Cancel.IsEnabled = false; row.State.Text = Loc.T("Stopping…"); _operation?.Cancel(); };
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("Model status could not be read. Reopen these settings to retry."); }
        finally
        {
            _busy = false;
            if (_refreshPending) { _refreshPending = false; ModelStateChanged(); }
        }
    }

    private void ModelStateChanged() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!IsLoaded) return;
        if (_busy) { _refreshPending = true; return; }
        await RefreshAsync();
    });

    private void DownloadChanged() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!IsLoaded) return;
        if (!ShowActiveDownload())
        {
            if (_session.LocalLlmDownloadPluginId == _pluginId)
                _status.Text = _session.LocalLlmDownload.State.Message ?? "";
            await RefreshAsync();
        }
    });

    private bool ShowActiveDownload()
    {
        var state = _session.LocalLlmDownload.State;
        if (!state.IsBusy || _session.LocalLlmDownloadPluginId != _pluginId) return false;
        _content.Children.Clear(); _rows.Clear();
        _status.Text = state.Progress is { } fraction
            ? Loc.T("Downloading {0} · {1:P0}", _session.LocalLlmDownloadModelName, fraction)
            : Loc.T("Downloading {0}…", _session.LocalLlmDownloadModelName);
        _downloadProgress.IsIndeterminate = state.Progress is null;
        _downloadProgress.Value = (state.Progress ?? 0) * 100;
        _content.Children.Add(_status); _content.Children.Add(_downloadProgress); _content.Children.Add(_cancelDownload);
        return true;
    }

    private bool Current(CancellationTokenSource lifetime) => IsLoaded && ReferenceEquals(_lifetime, lifetime) && !lifetime.IsCancellationRequested;

    private async Task RunAsync(Row row, string action)
    {
        if (_busy || _lifetime is not { } lifetime || !_session.CanStartPluginSettingsAction) return;
        if (action == "download")
        {
            try { await _session.DownloadLocalLlmModelAsync(_pluginId, row.Model.Model.Id, row.Model.Model.DisplayName); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (Current(lifetime)) _status.Text = Loc.T("Model download could not start: {0}", ex.Message); }
            if (IsLoaded) await RefreshAsync();
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _operation = operation;
        void CancelForRecording() => operation.Cancel();
        if (action is "remove" or "load") _session.RecordingStarting += CancelForRecording;
        _busy = true;
        foreach (var item in _rows) foreach (var button in item.Actions.Children.OfType<Control>()) button.IsEnabled = false;
        try
        {
            if (action == "remove")
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                    Title = Loc.T("Remove {0}?", row.Model.Model.DisplayName),
                    Content = Loc.T("The model will be unloaded and its downloaded file removed. You can download it again later."),
                    PrimaryButtonText = Loc.T("Remove model"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || !Current(lifetime)) return;
            }
            operation.Token.ThrowIfCancellationRequested();
            row.Progress.Visibility = Visibility.Visible; row.Progress.IsIndeterminate = true;
            row.State.Text = action switch { "download" => Loc.T("Downloading…"), "load" => Loc.T("Loading model into memory…"), "unload" => Loc.T("Releasing model memory…"), _ => Loc.T("Removing downloaded file…") };
            row.Cancel.Visibility = Visibility.Visible; row.Cancel.IsEnabled = true;
            try
            {
                // The runtime remembers a loaded model so it can load again after an idle release or a restart.
                var runtime = _session.PluginRuntime;
                await (action switch
                {
                    "load" => runtime.LoadLocalLlmModelAsync(_pluginId, row.Model.Model.Id, operation.Token),
                    "unload" => runtime.UnloadLocalLlmModelAsync(_pluginId, row.Model.Model.Id, operation.Token),
                    _ => runtime.RemoveLocalLlmModelAsync(_pluginId, row.Model.Model.Id, operation.Token)
                });
                if (Current(lifetime)) _status.Text = action == "load" ? Loc.T("Model loaded. Select it in a text-processing workflow.") : Loc.T("Completed.");
            }
            finally { row.Cancel.IsEnabled = false; }
        }
        catch (OperationCanceledException) { if (Current(lifetime)) _status.Text = Loc.T("Model operation cancelled."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = Loc.T("Model operation failed: {0}", ex.Message); }
        finally
        {
            if (action is "remove" or "load") _session.RecordingStarting -= CancelForRecording;
            _operation = null;
            _busy = false;
            if (IsLoaded) await RefreshAsync();
        }
    }

    private sealed class Row
    {
        internal readonly LocalLlmModelState Model;
        // A hairline separates each row from the heading or the row above.
        internal readonly Border Panel = new() { Padding = new(0, 14, 0, 14), BorderThickness = new(0, 1, 0, 0) };
        internal readonly StackPanel Actions = new() { Spacing = 8, Orientation = Orientation.Horizontal };
        internal readonly TextBlock State;
        internal readonly ProgressBar Progress = new() { Minimum = 0, Maximum = 100, Height = 6, Margin = new(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        internal readonly HandCursorButton Download = Button(Loc.T("Download model"));
        internal readonly HandCursorButton Load = Button(Loc.T("Load model"));
        internal readonly HandCursorButton Unload = Button(Loc.T("Unload model"));
        internal readonly HandCursorButton Remove = Button(Loc.T("Remove"));
        internal readonly HandCursorButton Cancel = Button(Loc.T("Cancel"));
        // A remembered model is released from memory but loads again on its next use.
        internal Row(LocalLlmModelState model, bool remembered)
        {
            Model = model;
            var layout = new Grid { ColumnSpacing = 14 };
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var name = Label(model.Model.DisplayName); name.FontSize = 15; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var badgeText = Label(model.Loaded ? Loc.T("Loaded") : Loc.T("Ready")); badgeText.FontSize = 11; badgeText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var badge = new Border { Child = badgeText, Padding = new(8, 2, 8, 3), CornerRadius = new(6), VerticalAlignment = VerticalAlignment.Center,
                Visibility = model.Loaded || remembered ? Visibility.Visible : Visibility.Collapsed };
            // Left-aligned, the star column is as wide as the name but still wraps a long one.
            var title = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            title.Children.Add(name); Grid.SetColumn(badge, 1); title.Children.Add(badge);
            copy.Children.Add(title);
            var meta = Label(string.Join(" · ", new[] { model.Model.SizeDescription, model.Model.IsRecommended ? Loc.T("Recommended") : null }
                .Where(fact => !string.IsNullOrWhiteSpace(fact))), true);
            meta.Visibility = meta.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            copy.Children.Add(meta);
            // The buttons already say whether a model is downloaded; the note explains an idle release or a running operation.
            State = Label(remembered ? Loc.T("Released while idle. It loads again on its next use.") : "", true);
            State.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => State.Visibility = State.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
            State.Visibility = State.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            copy.Children.Add(State); copy.Children.Add(Progress);
            layout.Children.Add(copy);
            Download.Visibility = model.Downloaded ? Visibility.Collapsed : Visibility.Visible;
            Load.Visibility = model.Downloaded && !model.Loaded && !remembered ? Visibility.Visible : Visibility.Collapsed;
            Unload.Visibility = model.Loaded || remembered ? Visibility.Visible : Visibility.Collapsed;
            Remove.Visibility = model.Downloaded ? Visibility.Visible : Visibility.Collapsed;
            Cancel.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(Download, Loc.T("Download missing files or verify an existing copy."));
            Download.Style = Load.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
            foreach (var button in new[] { Download, Load, Unload, Remove }) Actions.Children.Add(button);
            // Cancel stays outside Actions, which are disabled as a group during an operation.
            var side = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            side.Children.Add(Actions); side.Children.Add(Cancel);
            Grid.SetColumn(side, 1); layout.Children.Add(side);
            Panel.Child = layout;
            Panel.SizeChanged += (_, e) =>
            {
                var narrow = e.NewSize.Width < 480;
                Grid.SetColumnSpan(copy, narrow ? 2 : 1);
                Grid.SetColumn(side, narrow ? 0 : 1); Grid.SetRow(side, narrow ? 1 : 0);
                side.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            };
            void Theme()
            {
                Panel.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
                badge.Background = (Brush)Application.Current.Resources["ElevatedBrush"];
                badgeText.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
            }
            Panel.ActualThemeChanged += (_, _) => Theme(); Theme();
            AutomationProperties.SetName(Progress, Loc.T("{0} download progress", model.Model.DisplayName));
            AutomationProperties.SetName(Download, Loc.T("Download {0}", model.Model.DisplayName));
            AutomationProperties.SetName(Load, Loc.T("Load {0}", model.Model.DisplayName));
            AutomationProperties.SetName(Remove, Loc.T("Remove {0}", model.Model.DisplayName));
        }
    }
    private static TextBlock Label(string text, bool muted = false) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"] };
    private static HandCursorButton Button(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
        Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
}
