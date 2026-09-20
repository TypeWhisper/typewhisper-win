using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

internal sealed class LiveLocalTtsModelSettings : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly string _pluginId;
    private readonly StackPanel _licenses = new() { Spacing = 8 };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _description = Note("");
    private readonly TextBlock _state = Note("");
    private readonly TextBlock _message = Note("");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly StackPanel _actions = new() { Spacing = 8 };
    private readonly HandCursorButton _download = Button("Download & Load");
    private readonly HandCursorButton _unload = Button("Unload model");
    private readonly HandCursorButton _cancel = Button("Cancel download");
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _downloaded;
    private bool _loaded;
    private bool _accepted;

    internal LiveLocalTtsModelSettings(LocalDictationSession session, string pluginId)
    {
        _session = session; _pluginId = pluginId;
        Visibility = Visibility.Collapsed;
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(_title); content.Children.Add(_description); content.Children.Add(_licenses);
        content.Children.Add(_state); content.Children.Add(_progress);
        _actions.Children.Add(_download); _actions.Children.Add(_unload);
        content.Children.Add(_actions); content.Children.Add(_cancel); content.Children.Add(_message);
        Content = new Border { Child = content, Padding = new(18), CornerRadius = new(12), BorderThickness = new(1),
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
            Background = (Brush)Application.Current.Resources["SurfaceBrush"] };
        SizeChanged += (_, e) => _actions.Orientation = e.NewSize.Width < 440 ? Orientation.Vertical : Orientation.Horizontal;
        AutomationProperties.SetLiveSetting(_state, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, "Speech model download progress");
        _download.Click += async (_, _) => await RunAsync(async (model, ct) =>
        {
            var operation = _operation;
            var progress = new CallbackProgress(value => DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsLoaded || !ReferenceEquals(_operation, operation) || operation?.IsCancellationRequested != false) return;
                var percent = Math.Clamp(double.IsFinite(value) ? value * 100 : 0, 0, 100);
                _progress.Value = percent;
                _state.Text = percent >= 100 ? "Download complete · Loading model…" : $"Downloading · {percent:0}%";
            }));
            await model.DownloadAndLoadModelAsync(progress, ct);
        }, download: true);
        _unload.Click += async (_, _) => await RunAsync((model, ct) => model.UnloadModelAsync(ct));
        _cancel.Click += (_, _) => { _operation?.Cancel(); _cancel.IsEnabled = false; _state.Text = "Cancelling…"; };
        Loaded += async (_, _) =>
        {
            _lifetime = new();
            session.Changed += UpdateButtons;
            await ReadAsync();
        };
        Unloaded += (_, _) =>
        {
            session.Changed -= UpdateButtons;
            _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        };
    }

    private async Task ReadAsync()
    {
        if (_lifetime is not { } lifetime || _busy) return;
        try
        {
            var state = await _session.PluginRuntime.UseConfigurationAsync(_pluginId, (plugin, _) =>
                Task.FromResult(plugin is ILocalTtsModelManagement model
                    ? (model.LocalModel, model.IsModelDownloaded, model.IsModelLoaded, model.ModelDownloadRequirements.ToArray())
                    : ((PluginModelInfo?)null, false, false, Array.Empty<PluginModelDownloadRequirement>())), lifetime.Token);
            if (!IsLoaded || !ReferenceEquals(_lifetime, lifetime)) return;
            if (state.Item1 is not { } info) { Visibility = Visibility.Collapsed; return; }
            Visibility = Visibility.Visible;
            _downloaded = state.Item2; _loaded = state.Item3;
            _accepted = state.Item4.Where(r => r.IsRequired).All(r => r.IsSatisfied);
            _title.Text = info.DisplayName;
            _description.Text = $"Local speech output · {info.SizeDescription}\n{info.LanguageCount} languages · Audio is generated on this device.";
            _licenses.Children.Clear();
            foreach (var requirement in state.Item4.Where(r => r.Kind == PluginModelDownloadRequirementKind.License))
            {
                _licenses.Children.Add(Note(requirement.Description));
                if (requirement.MoreInfoUri is { Scheme: "https" } uri)
                    _licenses.Children.Add(new HyperlinkButton { Content = "Read model license", NavigateUri = uri,
                        HorizontalAlignment = HorizontalAlignment.Left, Padding = new(0) });
                var check = new CheckBox { Content = new TextBlock { Text = "I have read and accept the model license terms", TextWrapping = TextWrapping.Wrap },
                    IsChecked = requirement.IsSatisfied, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(check, "Accept " + info.DisplayName + " model license");
                check.Click += async (_, _) =>
                {
                    var accepted = check.IsChecked == true;
                    await RunAsync((model, ct) => model.SetModelDownloadLicenseAcceptanceAsync(info.Id, requirement.Id, accepted, ct));
                };
                _licenses.Children.Add(check);
            }
            _progress.Visibility = _downloaded ? Visibility.Visible : Visibility.Collapsed;
            _progress.Value = _downloaded ? 100 : 0;
            _state.Text = _loaded ? "Ready · Model loaded" : _downloaded ? "Downloaded · 100%" : "Download required";
            UpdateButtons();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsLoaded) { Visibility = Visibility.Visible; _message.Text = "Model status could not be read. Reopen this page to retry."; } }
    }

    private async Task RunAsync(Func<ILocalTtsModelManagement, CancellationToken, Task> action, bool download = false)
    {
        if (_busy || !_session.CanStartPluginSettingsAction || _lifetime is not { } lifetime) { UpdateButtons(); return; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _operation = operation; _busy = true; _message.Text = "";
        if (download)
        {
            _progress.Visibility = Visibility.Visible;
            _progress.Value = _downloaded ? 100 : 0;
            _state.Text = _downloaded ? "Loading model…" : "Downloading · 0%";
        }
        _cancel.Content = download ? "Cancel download / loading" : "Cancel";
        void CancelForRecording() => operation.Cancel();
        _session.RecordingStarting += CancelForRecording;
        UpdateButtons();
        try
        {
            await _session.PluginRuntime.UseConfigurationAsync(_pluginId, async (plugin, ct) =>
            {
                if (plugin is not ILocalTtsModelManagement model) throw new NotSupportedException();
                await action(model, ct); return true;
            }, operation.Token);
        }
        catch (OperationCanceledException)
        { if (IsLoaded) _message.Text = "Operation cancelled. Downloaded files are kept for the next attempt."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsLoaded) _message.Text = "The model operation failed. Check the connection and available disk space, then retry."; }
        finally
        {
            _session.RecordingStarting -= CancelForRecording;
            _operation = null; _busy = false;
            if (IsLoaded && ReferenceEquals(_lifetime, lifetime)) await ReadAsync();
        }
    }

    private void UpdateButtons()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(UpdateButtons); return; }
        var available = !_busy && _session.CanStartPluginSettingsAction;
        _licenses.IsEnabled = available;
        _download.Content = _downloaded ? "Load model" : "Download & Load";
        _download.IsEnabled = available && _accepted && !_loaded;
        _download.Visibility = _loaded ? Visibility.Collapsed : Visibility.Visible;
        _unload.IsEnabled = available && _loaded;
        _unload.Visibility = _loaded ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = _busy && _operation?.IsCancellationRequested == false;
    }

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private static HandCursorButton Button(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
        Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
}
