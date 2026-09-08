using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

internal sealed class DictationReviewWindow : Window
{
    private readonly ManualPluginActionController _actionController = new();
    private readonly TextBlock _actionStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _actionPicker = new() { MinWidth = 180, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly HandCursorButton _runAction = new();
    private Task? _shutdownTask;
    private bool _closing;
    private bool _allowClose;

    internal DictationReviewWindow(DictationOutputResult result, PortablePluginRuntimeRegistry? registry = null)
    {
        NativeWindowAppearance.ApplyAppTitleBar(this);
        Title = "Review dictation · TypeWhisper";
        AppWindow.Resize(new SizeInt32(680, 460));
        var body = new Grid { Padding = new Thickness(24), RowSpacing = 16,
            Background = (Brush)Application.Current.Resources["InkBrush"] };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = "Review dictation", FontSize = 24,
            Foreground = (Brush)Application.Current.Resources["TextBrush"] });
        var status = new TextBlock { Text = result.Message + (result.Saved ? "" : " Closing this window discards this review copy."),
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        heading.Children.Add(status);
        body.Children.Add(heading);
        var transcript = new TextBox { AcceptsReturn = true, IsReadOnly = true, Text = result.Record.FinalText.ReplaceLineEndings("\r"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12),
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            FontFamily = (FontFamily)Application.Current.Resources["InterfaceFont"] };
        AutomationProperties.SetName(transcript, "Dictation result");
        ScrollViewer.SetVerticalScrollBarVisibility(transcript, ScrollBarVisibility.Auto);
        Grid.SetRow(transcript, 1); body.Children.Add(transcript);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new HandCursorButton { Content = "Copy text", Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(result.Record.FinalText);
                Clipboard.SetContent(data);
                status.Text = "Copied. " + (result.Saved ? "Saved to History." : "Not saved to History.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { status.Text = "Clipboard unavailable. Your text is still here; try again."; }
        };
        var close = new HandCursorButton { Content = "Done", Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        close.Click += async (_, _) =>
        {
            try { await ShutdownAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _actionStatus.Text = "This review could not finish closing. Wait for the action to finish and try closing TypeWhisper again."; }
        };
        actions.Children.Add(copy); actions.Children.Add(close);
        var footer = new StackPanel { Spacing = 10 };
        var available = registry?.Actions.ToArray() ?? [];
        var pluginActions = new StackPanel { Spacing = 8, Visibility = available.Length == 0 ? Visibility.Collapsed : Visibility.Visible };
        pluginActions.Children.Add(new TextBlock { Text = "Manual action", Foreground = (Brush)Application.Current.Resources["TextBrush"] });
        AutomationProperties.SetName(_actionPicker, "Manual plugin action");
        foreach (var action in available) _actionPicker.Items.Add(new ComboBoxItem { Content = action.Name, Tag = action });
        _runAction.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
        _runAction.Content = "Run action";
        _actionPicker.SelectionChanged += (_, _) =>
        {
            if (_actionPicker.SelectedItem is ComboBoxItem { Tag: PortablePluginAction selected })
                _runAction.Content = selected.Name;
        };
        if (available.Length > 0) _actionPicker.SelectedIndex = 0;
        var pluginButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pluginButtons.Children.Add(_actionPicker); pluginButtons.Children.Add(_runAction);
        pluginActions.Children.Add(pluginButtons);
        footer.Children.Add(pluginActions);
        _actionStatus.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        AutomationProperties.SetLiveSetting(_actionStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        footer.Children.Add(_actionStatus);
        footer.Children.Add(actions);
        _runAction.Click += async (_, _) =>
        {
            if (_closing || _actionController.IsRunning || registry is null ||
                _actionPicker.SelectedItem is not ComboBoxItem { Tag: PortablePluginAction selected }) return;
            _runAction.IsEnabled = _actionPicker.IsEnabled = false;
            _actionStatus.Text = "Running " + selected.Name + "…";
            try
            {
                if (!registry.Actions.Contains(selected))
                {
                    _actionStatus.Text = "This action is no longer available. Your review text is unchanged.";
                    return;
                }
                var context = new ActionContext(result.Record.AppName, result.Record.AppProcessName,
                    null, result.Record.Language, null);
                var outcome = await _actionController.ExecuteAsync(result.Record.FinalText, async (text, ct) =>
                {
                    var response = await registry.ExecuteActionAsync(selected, text, context, ct);
                    return new ManualPluginActionOutcome(response.Status switch
                    {
                        PortableActionStatus.Succeeded => ManualPluginActionStatus.Succeeded,
                        PortableActionStatus.Failed => ManualPluginActionStatus.Failed,
                        _ => ManualPluginActionStatus.CompletionUnknown
                    }, response.Message);
                });
                if (!_closing) _actionStatus.Text = outcome.Status switch
                {
                    ManualPluginActionStatus.Succeeded => "Completed: " + outcome.Message,
                    ManualPluginActionStatus.Failed => "Action failed: " + outcome.Message,
                    ManualPluginActionStatus.Canceled => outcome.Message,
                    _ => "Completion unknown. " + outcome.Message
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!_closing) _actionStatus.Text = "The action could not finish. Check its destination before running it again. Your review text is unchanged."; }
            finally
            {
                if (!_closing) _runAction.IsEnabled = _actionPicker.IsEnabled = true;
            }
        };
        Grid.SetRow(footer, 2); body.Children.Add(footer);
        Content = body;
        if (available.Length > 0) AppWindow.Resize(new SizeInt32(680, 560));
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            try { await ShutdownAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _actionStatus.Text = "The action has not finished closing. Your review remains open."; }
        };
    }

    // Called on the window's UI thread. Closing is deferred beyond input callbacks and waits for the action lease.
    internal Task ShutdownAsync()
    {
        if (_shutdownTask is not null) return _shutdownTask;
        _closing = true;
        _runAction.IsEnabled = _actionPicker.IsEnabled = false;
        _actionStatus.Text = _actionController.IsRunning
            ? "Closing: cancellation requested. Waiting for the action to finish; any saved output will remain at its destination."
            : "Closing review…";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdownTask = completion.Task;
        _ = DrainAndCloseAsync(completion);
        return _shutdownTask;
    }

    private async Task DrainAndCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            await _actionController.ShutdownAsync();
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { _allowClose = true; Close(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
            })) completion.TrySetException(new InvalidOperationException("The review dispatcher is unavailable."));
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
