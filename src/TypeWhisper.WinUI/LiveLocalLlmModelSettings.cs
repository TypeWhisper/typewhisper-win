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
    private readonly TextBlock _status = Label("");
    private readonly List<Row> _rows = [];
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _operation;
    private bool _busy;

    internal LiveLocalLlmModelSettings(LocalDictationSession session, string pluginId)
    {
        _session = session; _pluginId = pluginId;
        Content = _content;
        _content.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Loaded += async (_, _) => { _lifetime = new(); await RefreshAsync(); };
        Unloaded += (_, _) => { _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; };
    }

    private async Task RefreshAsync()
    {
        if (_lifetime is not { } lifetime || _busy) return;
        _busy = true;
        try
        {
            var models = await _session.PluginRuntime.UseConfigurationAsync(_pluginId, (plugin, _) =>
                Task.FromResult((plugin as ILocalLlmModelManagement)?.LocalModels.ToArray() ?? []), lifetime.Token);
            if (!Current(lifetime)) return;
            _rows.Clear(); _content.Children.Clear();
            if (models.Length == 0)
            {
                _status.Text = "No local text models are currently available. Re-enable the plugin or reopen this page to retry.";
                _content.Children.Add(_status);
                return;
            }
            _content.Children.Add(Label("Local text processing · CPU\nDownload a model, then load it to use it in a workflow. Your text stays on this device."));
            _content.Children.Add(_status);
            foreach (var model in models)
            {
                var row = new Row(model);
                _rows.Add(row); _content.Children.Add(row.Panel);
                row.Download.Click += async (_, _) => await RunAsync(row, "download");
                row.Load.Click += async (_, _) => await RunAsync(row, "load");
                row.Unload.Click += async (_, _) => await RunAsync(row, "unload");
                row.Remove.Click += async (_, _) => await RunAsync(row, "remove");
                row.Cancel.Click += (_, _) => { row.Cancel.IsEnabled = false; row.State.Text = "Stopping…"; _operation?.Cancel(); };
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = "Model status could not be read. Reopen these settings to retry."; }
        finally { _busy = false; }
    }

    private bool Current(CancellationTokenSource lifetime) => IsLoaded && ReferenceEquals(_lifetime, lifetime) && !lifetime.IsCancellationRequested;

    private async Task RunAsync(Row row, string action)
    {
        if (_busy || _lifetime is not { } lifetime || !_session.CanStartPluginSettingsAction) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _operation = operation;
        void CancelForRecording() => operation.Cancel();
        _session.RecordingStarting += CancelForRecording;
        _session.LlmProcessingStarting += CancelForRecording;
        _busy = true;
        foreach (var item in _rows) foreach (var button in item.Actions.Children.OfType<Control>()) button.IsEnabled = false;
        try
        {
            if (action == "remove")
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                    Title = "Remove " + row.Model.Model.DisplayName + "?",
                    Content = "The model will be unloaded and its downloaded file removed. You can download it again later.",
                    PrimaryButtonText = "Remove model", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || !Current(lifetime)) return;
            }
            operation.Token.ThrowIfCancellationRequested();
            row.Progress.Visibility = Visibility.Visible; row.Progress.IsIndeterminate = true;
            row.State.Text = action switch { "download" => "Downloading…", "load" => "Loading model into memory…", "unload" => "Releasing model memory…", _ => "Removing downloaded file…" };
            row.Cancel.Visibility = Visibility.Visible; row.Cancel.IsEnabled = true;
            var progress = new Progress<double>(value =>
            {
                if (!Current(lifetime) || !ReferenceEquals(_operation, operation) || operation.IsCancellationRequested || !double.IsFinite(value)) return;
                row.Progress.IsIndeterminate = false; row.Progress.Value = Math.Clamp(value, 0, 1) * 100;
                row.State.Text = $"Downloading · {Math.Clamp(value, 0, 1):P0}";
            });
            try
            {
                await _session.PluginRuntime.UseConfigurationAsync(_pluginId, async (plugin, ct) =>
                {
                    if (plugin is not ILocalLlmModelManagement local || !local.LocalModels.Any(m => m.Model.Id == row.Model.Model.Id))
                        throw new InvalidOperationException("The local model provider changed.");
                    switch (action)
                    {
                        case "download": await local.DownloadModelAsync(row.Model.Model.Id, progress, ct); break;
                        case "load": await local.LoadModelAsync(row.Model.Model.Id, ct); break;
                        case "unload":
                            if (local.LocalModels.Any(m => m.Model.Id == row.Model.Model.Id && m.Loaded)) await local.UnloadModelAsync(ct);
                            break;
                        case "remove": await local.RemoveModelAsync(row.Model.Model.Id, ct); break;
                    }
                    return true;
                }, operation.Token, preserveCompletedResult: true);
                if (Current(lifetime)) _status.Text = action == "load" ? "Model loaded. Select it in a text-processing workflow." : "Completed.";
            }
            finally { row.Cancel.IsEnabled = false; }
        }
        catch (OperationCanceledException) { if (Current(lifetime)) _status.Text = "Model operation cancelled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (Current(lifetime)) _status.Text = "Model operation failed: " + ex.Message; }
        finally
        {
            _session.RecordingStarting -= CancelForRecording;
            _session.LlmProcessingStarting -= CancelForRecording;
            _operation = null;
            _busy = false;
            if (IsLoaded) await RefreshAsync();
        }
    }

    private sealed class Row
    {
        internal readonly LocalLlmModelState Model;
        internal readonly Border Panel = new() { Padding = new(18), CornerRadius = new(12), BorderThickness = new(1) };
        internal readonly StackPanel Actions = new() { Spacing = 8 };
        internal readonly TextBlock State;
        internal readonly ProgressBar Progress = new() { Minimum = 0, Maximum = 100, Height = 6, Visibility = Visibility.Collapsed };
        internal readonly HandCursorButton Download = Button("Download model");
        internal readonly HandCursorButton Load = Button("Load model");
        internal readonly HandCursorButton Unload = Button("Unload model");
        internal readonly HandCursorButton Remove = Button("Remove model");
        internal readonly HandCursorButton Cancel = Button("Cancel");
        internal Row(LocalLlmModelState model)
        {
            Model = model;
            var body = new StackPanel { Spacing = 12 };
            var title = Label(model.Model.DisplayName); title.FontSize = 16; title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            body.Children.Add(title);
            body.Children.Add(Label(model.Model.SizeDescription + (model.Model.IsRecommended ? " · Recommended" : "")));
            State = Label(model.Loaded ? "Loaded · ready for text processing" : model.Downloaded ? "Downloaded · 100%" : "Not downloaded");
            body.Children.Add(State); body.Children.Add(Progress);
            Download.Visibility = model.Downloaded ? Visibility.Collapsed : Visibility.Visible;
            Load.Visibility = model.Downloaded && !model.Loaded ? Visibility.Visible : Visibility.Collapsed;
            Unload.Visibility = model.Loaded ? Visibility.Visible : Visibility.Collapsed;
            Remove.Visibility = model.Downloaded ? Visibility.Visible : Visibility.Collapsed;
            Cancel.Visibility = Visibility.Collapsed;
            Download.Style = Load.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
            foreach (var button in new[] { Download, Load, Unload, Remove }) Actions.Children.Add(button);
            body.Children.Add(Actions); body.Children.Add(Cancel); Panel.Child = body;
            Panel.SizeChanged += (_, e) => Actions.Orientation = e.NewSize.Width < 440 ? Orientation.Vertical : Orientation.Horizontal;
            void Theme()
            {
                Panel.Background = (Brush)Application.Current.Resources["SurfaceBrush"];
                Panel.BorderBrush = (Brush)Application.Current.Resources[model.Loaded ? "AccentBrush" : "HairlineBrush"];
            }
            Panel.ActualThemeChanged += (_, _) => Theme(); Theme();
            AutomationProperties.SetName(Progress, model.Model.DisplayName + " download progress");
            AutomationProperties.SetName(Download, "Download " + model.Model.DisplayName);
            AutomationProperties.SetName(Load, "Load " + model.Model.DisplayName);
            AutomationProperties.SetName(Remove, "Remove " + model.Model.DisplayName);
        }
    }
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private static HandCursorButton Button(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
        Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
}
