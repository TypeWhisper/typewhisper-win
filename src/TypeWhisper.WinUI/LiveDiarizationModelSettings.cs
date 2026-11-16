using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

// The models of a plugin's diarization provider: download with progress and cancellation, and removal. Downloads go
// through the plugin runtime, which applies the model download policy.
internal sealed class LiveDiarizationModelSettings : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly string _pluginId;
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = Note("");
    private readonly TextBlock _state = Note("");
    private readonly TextBlock _message = Note("");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly HandCursorButton _download = Button(Loc.T("Download model"));
    private readonly HandCursorButton _remove = Button(Loc.T("Remove model"));
    private readonly HandCursorButton _cancel = Button(Loc.T("Cancel download"));
    private PortableDiarizationProvider? _provider;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _operation;
    private bool _busy;

    internal LiveDiarizationModelSettings(LocalDictationSession session, string pluginId)
    {
        _session = session; _pluginId = pluginId;
        Visibility = Visibility.Collapsed;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_download); actions.Children.Add(_remove); actions.Children.Add(_cancel);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(_title); content.Children.Add(_description); content.Children.Add(_state);
        content.Children.Add(_progress); content.Children.Add(actions); content.Children.Add(_message);
        var card = new Border { Child = content, Padding = new(18), CornerRadius = new(12), BorderThickness = new(1) };
        void Theme()
        {
            card.Background = (Brush)Application.Current.Resources["SurfaceBrush"];
            card.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
        }
        card.ActualThemeChanged += (_, _) => Theme(); Theme();
        Content = card;
        SizeChanged += (_, e) => actions.Orientation = e.NewSize.Width < 440 ? Orientation.Vertical : Orientation.Horizontal;
        AutomationProperties.SetLiveSetting(_state, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _download.Click += async (_, _) => await RunAsync(download: true);
        _remove.Click += async (_, _) => await RunAsync(download: false);
        _cancel.Click += (_, _) => { _operation?.Cancel(); _cancel.IsEnabled = false; };
        Loaded += (_, _) =>
        {
            _lifetime = new();
            session.Changed += Update;
            Update();
        };
        Unloaded += (_, _) =>
        {
            session.Changed -= Update;
            _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        };
    }

    // Reads the published snapshot only; it never runs the plugin.
    private void Update()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(Update); return; }
        if (!IsLoaded) return;
        _provider = _session.PluginRuntime.DiarizationProviders.FirstOrDefault(provider => provider.PluginId == _pluginId);
        if (_provider is not { } provider) { Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        var model = provider.Model?.DisplayName ?? provider.Name;
        _title.Text = provider.Model?.SizeDescription is { Length: > 0 } size ? model + " · " + size : model;
        _description.Text = provider.IsLocal
            ? Loc.T("Tells who spoke when in a recording. Audio stays on this device.")
            : Loc.T("Tells who spoke when in a recording. Audio is sent to this provider.");
        AutomationProperties.SetName(_progress, Loc.T("{0} download progress", model));
        if (!_busy)
        {
            _state.Text = provider.ModelsInstalled ? Loc.T("Ready") : Loc.T("Download required");
            _progress.Visibility = Visibility.Collapsed;
        }
        UpdateButtons();
    }

    private async Task RunAsync(bool download)
    {
        if (_busy || _provider is not { } provider || _lifetime is not { } lifetime || !_session.CanStartPluginSettingsAction) { UpdateButtons(); return; }
        if (download && LocalDictationSession.ModelDownloadRefusal() is { } refused) { _message.Text = refused; UpdateButtons(); return; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _operation = operation; _busy = true; _message.Text = "";
        _state.Text = download ? Loc.T("Downloading · {0:0}%", 0) : Loc.T("Removing downloaded files…");
        _progress.Value = 0;
        _progress.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
        void Cancel() => operation.Cancel();
        // A download also stops when the policy turns downloads off.
        if (download) _session.ModelDownloadsRefused += Cancel;
        UpdateButtons();
        try
        {
            if (download)
            {
                var progress = new CallbackProgress(value => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsLoaded || !ReferenceEquals(_operation, operation) || operation.IsCancellationRequested) return;
                    var percent = Math.Clamp(double.IsFinite(value) ? value * 100 : 0, 0, 100);
                    _progress.Value = percent;
                    _state.Text = Loc.T("Downloading · {0:0}%", percent);
                }));
                await _session.PluginRuntime.PrepareDiarizationModelsAsync(provider.ProviderId, progress, operation.Token);
            }
            else await _session.PluginRuntime.DeleteDiarizationModelsAsync(provider.ProviderId, operation.Token);
        }
        catch (TypeWhisper.Core.Models.ManagedPolicyDeniedException error)
        { if (IsLoaded && ReferenceEquals(_lifetime, lifetime)) _message.Text = error.Message; }
        catch (OperationCanceledException)
        { if (IsLoaded && ReferenceEquals(_lifetime, lifetime)) _message.Text = Loc.T("Operation cancelled. Downloaded files are kept for the next attempt."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.WriteFailure(AppDiagnostics.PluginStage("plugin.diarization.models-failed", _pluginId), ex);
            if (IsLoaded && ReferenceEquals(_lifetime, lifetime)) _message.Text = Loc.T("The model operation failed. Check the connection and available disk space, then retry.");
        }
        finally
        {
            if (download) _session.ModelDownloadsRefused -= Cancel;
            _operation = null; _busy = false;
            // The runtime publishes the new model state with its next refresh, which raises Changed.
            Update();
        }
    }

    private void UpdateButtons()
    {
        var installed = _provider?.ModelsInstalled == true;
        var available = !_busy && _session.CanStartPluginSettingsAction && _provider is not null;
        var downloadRefusal = installed ? null : LocalDictationSession.ModelDownloadRefusal();
        _download.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        _download.IsEnabled = available && downloadRefusal is null;
        ToolTipService.SetToolTip(_download, downloadRefusal);
        _remove.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        _remove.IsEnabled = available;
        _cancel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = _busy && _operation?.IsCancellationRequested == false;
    }

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private static HandCursorButton Button(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
        Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
}
