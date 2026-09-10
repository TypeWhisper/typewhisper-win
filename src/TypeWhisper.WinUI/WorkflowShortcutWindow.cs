using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

internal sealed class WorkflowShortcutWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBox _text;
    private readonly HandCursorButton _copy;
    private readonly HandCursorButton _cancel;
    private CancellationTokenSource? _run;
    private Task? _completion;
    private Task _cancelTask = Task.CompletedTask;
    private readonly object _cancelSync = new();
    private Task? _shutdownTask;
    private bool _closing;
    private bool _allowClose;
    internal WorkflowShortcutWindow(string name, string source, string provider)
    {
        Title = name + " · TypeWhisper";
        NativeWindowAppearance.ApplyAppTitleBar(this);
        AppWindow.Resize(new SizeInt32(680, 500));
        var body = new Grid { Padding = new Thickness(24), RowSpacing = 16, Background = Brush("InkBrush") };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = name, FontSize = 24, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextBrush") });
        _status = new() { Text = "Processing selected text with " + provider + "…", TextWrapping = TextWrapping.Wrap, Foreground = Brush("MutedBrush") };
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        heading.Children.Add(_status); body.Children.Add(heading);
        _text = new() { AcceptsReturn = true, IsReadOnly = true, Text = source.ReplaceLineEndings("\r"),
            TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12),
            Foreground = Brush("TextBrush"), Background = Brush("SurfaceBrush"), FontFamily = (FontFamily)Application.Current.Resources["InterfaceFont"] };
        AutomationProperties.SetName(_text, "Workflow selected text and result");
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        Grid.SetRow(_text, 1); body.Children.Add(_text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        _copy = new() { Content = "Copy text", IsEnabled = false, Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        _copy.Click += (_, _) =>
        {
            try { var data = new DataPackage(); data.SetText(_text.Text); Clipboard.SetContent(data); _status.Text = "Copied. Not saved to History."; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = "The clipboard is unavailable. Your text is still here."; }
        };
        _cancel = new() { Content = "Cancel", Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        _cancel.Click += async (_, _) =>
        {
            try
            {
                if (_run is not null) { RequestCancel(); _cancel.IsEnabled = false; _status.Text = "Canceling…"; }
                else await ShutdownAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _status.Text = "The workflow could not finish closing. Your text is still here."; }
        };
        buttons.Children.Add(_copy); buttons.Children.Add(_cancel); Grid.SetRow(buttons, 2); body.Children.Add(buttons);
        Content = body;
        AppWindow.Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            try { await ShutdownAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _status.Text = "The workflow could not finish closing. Your text is still here."; }
        };
    }
    internal Task RunAsync(Workflow workflow, LocalDictationSession session, CancellationToken ct)
    {
        if (_completion is not null) return _completion;
        if (_closing) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion.Task;
        _ = CompleteRunAsync(workflow, session, ct, completion);
        return completion.Task;
    }
    private async Task CompleteRunAsync(Workflow workflow, LocalDictationSession session, CancellationToken ct, TaskCompletionSource completion)
    {
        try { await RunCoreAsync(workflow, session, ct); completion.TrySetResult(); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    internal void ShowCaptureError(string error) { _status.Text = error; _cancel.Content = "Done"; }
    private async Task RunCoreAsync(Workflow workflow, LocalDictationSession session, CancellationToken ct)
    {
        using var cancellation = new CancellationTokenSource();
        lock (_cancelSync) _run = cancellation;
        // Forward parent cancellation without synchronously running SDK callbacks on its caller.
        var parentCancellation = ct.Register(RequestCancel);
        try
        {
            var result = await ManualWorkflowRunner.RunAsync(workflow, _text.Text,
                (provider, model) => session.LlmProviders.Any(p => p.SelectionId == provider && p.Ready && p.Models.Any(m => m.Id == model)),
                session.ProcessLlmAsync, cancellation.Token);
            ct.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            _text.Text = result.ReplaceLineEndings("\r");
            _status.Text = "Completed. Review and copy the result. Not saved to History.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_closing) _status.Text = "Canceled. Your selected text is still here; no result was applied."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_closing) _status.Text = "Processing failed. Your selected text is still here. " + ex.Message; }
        finally
        {
            await parentCancellation.DisposeAsync();
            Task callbacks;
            lock (_cancelSync) { _run = null; callbacks = _cancelTask; }
            await ObserveCancellationAsync(callbacks);
            _cancelTask = Task.CompletedTask;
            if (!_closing) { _copy.IsEnabled = true; _cancel.IsEnabled = true; _cancel.Content = "Done"; }
        }
    }
    private void RequestCancel()
    {
        lock (_cancelSync)
            if (_run is { IsCancellationRequested: false } request) _cancelTask = request.CancelAsync();
    }
    private static async Task ObserveCancellationAsync(Task callbacks)
    {
        try { await callbacks; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Workflow window cancellation callback failed: {0}", ex.GetType().Name); }
    }
    internal Task ShutdownAsync()
    {
        if (_shutdownTask is not null) return _shutdownTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdownTask = completion.Task;
        _closing = true;
        _copy.IsEnabled = _cancel.IsEnabled = false;
        _status.Text = "Finishing workflow cancellation…";
        RequestCancel();
        _ = ShutdownCoreAsync(completion);
        return completion.Task;
    }
    private async Task ShutdownCoreAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            if (_completion is not null) await _completion;
            await ObserveCancellationAsync(_cancelTask);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { _allowClose = true; Close(); }
            catch (Exception ex) { failure ??= ex; }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
