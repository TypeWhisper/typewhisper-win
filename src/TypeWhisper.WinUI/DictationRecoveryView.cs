using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

internal sealed class DictationRecoveryView : UserControl
{
    private readonly DictationRecoveryController _controller;
    private readonly DictationRecoveryPreferencesStore _preferences;
    private readonly Func<DictationRecoveryPreferences, Task<string?>> _commitPreferences;
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly TextBlock _notice = Label("");
    private ContentDialog? _dialog;
    private Task _uiOperation = Task.CompletedTask;
    private bool _working;
    private bool _closing;
    private bool _renderQueued;
    private string? _uiMessage;

    // The host supplies the real, drained preference/retention transaction. No UI-only setting.
    internal DictationRecoveryView(DictationRecoveryController controller,
        DictationRecoveryPreferencesStore preferences, Func<DictationRecoveryPreferences, Task<string?>> commitPreferences)
    {
        _controller = controller; _preferences = preferences; _commitPreferences = commitPreferences;
        Content = new ScrollViewer { Padding = (Thickness)Application.Current.Resources["VerticalScrollGutter"], Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);
        Loaded += (_, _) => { _controller.Changed += Changed; Render(); };
        Unloaded += (_, _) => _controller.Changed -= Changed;
        Render();
    }

    internal Task PresentAsync() => _controller.RefreshAsync();
    internal async Task ShutdownAsync(bool shutdownController = true)
    {
        _closing = true; IsEnabled = false; _dialog?.Hide();
        await Task.WhenAll(shutdownController ? _controller.ShutdownAsync() : _controller.CancelAndDrainAsync(), _uiOperation);
        _controller.Changed -= Changed;
    }

    private void Changed() => DispatcherQueue.TryEnqueue(QueueRender);

    private void QueueRender()
    {
        if (_closing || !IsLoaded || _renderQueued) return;
        _renderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _renderQueued = false;
            if (!_closing && IsLoaded && _dialog is null) Render();
        });
    }

    private void Render()
    {
        if (_closing) return;
        _body.Children.Clear();
        _body.Children.Add(Label(Loc.T("Dictation recovery"), 22));
        _body.Children.Add(Label(Loc.T("Keep microphone audio on this device to recover interrupted dictations. Recovery is separate from History and the Recorder library.")));
        var enabled = new ToggleSwitch { Header = Loc.T("Keep dictation audio for recovery"), IsOn = _preferences.Current.Enabled };
        var retention = new ChoicePicker();
        retention.Configure(Loc.T("Recovery audio retention"), "history", Loc.T("Recovery audio retention"));
        retention.SetOptions(new[] { 1, 7, 30, 60, 90, 180, 0 }.Select(days => new Choice(days.ToString(),
            days == 0 ? Loc.T("Keep until deleted") : Loc.T("{0} days", days), days == 0 ? Loc.T("No automatic expiry.") : Loc.T("Measured from when audio was recorded."))).ToArray(),
            _preferences.Current.RetentionDays.ToString());
        var locked = _working || _controller.Busy;
        enabled.IsEnabled = retention.IsEnabled = !locked;
        _body.Children.Add(enabled);
        _body.Children.Add(Label(Loc.T("Keep recovery audio for")));
        _body.Children.Add(retention);
        _body.Children.Add(Label(Loc.T("Turning recovery off keeps existing recordings. A shorter retention deletes older audio after confirmation. An interrupted recording may be missing its final moments.")));
        _body.Children.Add(Button(Loc.T("Save recovery preferences"), () => Start(async () =>
        {
            var next = new DictationRecoveryPreferences { Enabled = enabled.IsOn, RetentionDays = int.Parse(retention.SelectedId) };
            var old = _preferences.Current;
            if (next.Enabled && next.RetentionDays > 0 && (!old.Enabled || old.RetentionDays == 0 || next.RetentionDays < old.RetentionDays))
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-next.RetentionDays);
                var affected = _controller.Recordings.Count(recording => recording.CreatedAt < cutoff);
                if (!await Confirm(Loc.T("Apply recovery audio retention?"),
                    Loc.T("This applies a {0}-day limit. Currently {1} saved recovery recording(s) are older than {2:g} and will be permanently deleted. The age limit also applies to future recovery audio.", next.RetentionDays, affected, cutoff.LocalDateTime),
                    Loc.T("Apply retention"))) return;
            }
            if (_closing) return;
            var error = await _commitPreferences(next);
            if (!_closing) _uiMessage = error ?? Loc.T("Recovery preferences saved. Existing audio is not deleted when recovery is turned off.");
        }), locked));
        _body.Children.Add(Button(Loc.T("Refresh recordings"), () => Start(_controller.RefreshAsync), locked));
        if (_controller.Busy) _body.Children.Add(Button(Loc.T("Cancel recovery"), _controller.Cancel));
        _body.Children.Add(Label(_controller.Recordings.Count == 1 ? Loc.T("1 saved recovery recording") : Loc.T("{0} saved recovery recordings", _controller.Recordings.Count)));
        foreach (var recording in _controller.Recordings)
        {
            var row = new StackPanel { Spacing = 6 };
            var duration = TimeSpan.FromSeconds(recording.DurationSeconds);
            var elapsed = duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
            row.Children.Add(Label($"{recording.CreatedAt.LocalDateTime:g} · {elapsed}"));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Button(Loc.T("Transcribe and review"), () => Start(() => _controller.RetryAsync(recording.Id)), locked));
            actions.Children.Add(Button(Loc.T("Delete audio"), () => Start(async () =>
            {
                if (await Confirm(Loc.T("Delete this recovery recording?"), Loc.T("Delete the audio recorded {0:g}? This permanently deletes one recording. Reviewed text is kept.", recording.CreatedAt.LocalDateTime), Loc.T("Delete audio")) && !_closing)
                    await _controller.DeleteConfirmedAsync(recording.Id);
            }), locked));
            row.Children.Add(actions); _body.Children.Add(row);
        }
        if (_controller.Review is { } review)
        {
            _body.Children.Add(Label(Loc.T("Recovered text · review before copying"), 16));
            var text = new TextBox { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Text = review.Text.ReplaceLineEndings("\r"), MinHeight = 140, MaxHeight = 340,
                Style = (Style)Application.Current.Resources["LexiconMultilineStyle"] };
            AutomationProperties.SetName(text, Loc.T("Recovered dictation text"));
            _body.Children.Add(text);
            _body.Children.Add(Button(Loc.T("Copy reviewed text"), () =>
            {
                if (_closing) return;
                try { var data = new DataPackage(); data.SetText(review.Text); Clipboard.SetContent(data); _notice.Text = Loc.T("Text copied. Audio is still saved unless you deleted it."); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _notice.Text = Loc.T("Text could not be copied. Select it and copy manually."); }
            }));
        }
        _notice.Text = _uiMessage ?? _preferences.Error ?? _controller.Message ?? Loc.T("Recovery is off until you explicitly enable it.");
        _body.Children.Add(_notice);
    }

    private async Task<bool> Confirm(string title, string message, string primary)
    {
        if (_closing || XamlRoot is null) return false;
        _dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message,
            PrimaryButtonText = primary, CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close };
        try { return await _dialog.ShowAsync() == ContentDialogResult.Primary && !_closing; }
        finally { _dialog = null; }
    }
    private void Start(Func<Task> action)
    {
        if (_closing || _working || _controller.Busy) return;
        _uiMessage = null;
        _working = true;
        _uiOperation = Execute();
        // Keep the clicked control alive until WinUI finishes its pointer callback.
        QueueRender();
        async Task Execute()
        {
            try { await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (!_closing) _uiMessage = Loc.T("The recovery action could not finish. Your current review remains available."); }
            finally { _working = false; QueueRender(); }
        }
    }
    private static TextBlock Label(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static HandCursorButton Button(string text, Action action, bool disabled = false)
    {
        var button = new HandCursorButton { Content = text, IsEnabled = !disabled, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        button.Click += (_, _) => action(); return button;
    }
}
