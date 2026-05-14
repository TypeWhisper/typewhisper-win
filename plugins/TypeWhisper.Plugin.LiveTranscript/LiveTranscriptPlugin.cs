using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.LiveTranscript;

/// <summary>
/// Plugin that shows a floating window with real-time transcription text.
/// Subscribes to recording and transcription events to display live updates.
/// </summary>
public sealed class LiveTranscriptPlugin : ITypeWhisperPlugin
{
    private const string FontSizeSettingName = "fontSize";
    private const string OpacitySettingName = "opacity";
    private const string AutoHideMillisecondsSettingName = "autoHideMilliseconds";
    private const string WindowLeftSettingName = "windowLeft";
    private const string WindowTopSettingName = "windowTop";
    private const int DefaultAutoHideMilliseconds = 3000;
    private const int MinAutoHideMilliseconds = 0;
    private const int MaxAutoHideMilliseconds = 10000;
    private const int MaxTerminalRecordingIds = 64;

    private IPluginHostServices? _host;
    private LiveTranscriptWindow? _window;
    private readonly List<IDisposable> _subscriptions = [];
    private CancellationTokenSource? _autoHideCts;
    private readonly object _terminalRecordingLock = new();
    private readonly Queue<Guid> _terminalRecordingOrder = [];
    private readonly HashSet<Guid> _terminalRecordingIds = [];
    private bool _disposed;
    // Set to the RecordingId of the active recording; all handlers reject events
    // whose RecordingId differs so stale Task.Run callbacks can never mutate state
    // that belongs to a newer recording.
    private Guid? _activeRecordingId;

    public string PluginId => "com.typewhisper.live-transcript";
    public string PluginName => "Live Transcript";
    public string PluginVersion => "1.0.1";

    public int FontSize
    {
        get => _host?.GetSetting<int?>(FontSizeSettingName) ?? 16;
        set
        {
            _host?.SetSetting(FontSizeSettingName, value);
            if (_window is not null)
                Application.Current?.Dispatcher.InvokeAsync(() => _window.SetFontSize(value));
        }
    }

    public double Opacity
    {
        get => _host?.GetSetting<double?>(OpacitySettingName) ?? 0.85;
        set
        {
            _host?.SetSetting(OpacitySettingName, value);
            if (_window is not null)
                Application.Current?.Dispatcher.InvokeAsync(() => _window.SetWindowOpacity(value));
        }
    }

    public int AutoHideMilliseconds
    {
        get => NormalizeAutoHideMilliseconds(
            _host?.GetSetting<int?>(AutoHideMillisecondsSettingName) ?? DefaultAutoHideMilliseconds);
        set => _host?.SetSetting(AutoHideMillisecondsSettingName, NormalizeAutoHideMilliseconds(value));
    }

    private double? WindowLeft => _host?.GetSetting<double?>(WindowLeftSettingName);
    private double? WindowTop => _host?.GetSetting<double?>(WindowTopSettingName);

    public void SaveWindowPosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
            return;

        _host?.SetSetting(WindowLeftSettingName, left);
        _host?.SetSetting(WindowTopSettingName, top);
    }

    public void ResetWindowPosition()
    {
        _host?.SetSetting<double?>(WindowLeftSettingName, null);
        _host?.SetSetting<double?>(WindowTopSettingName, null);

        if (_window is not null)
            Application.Current?.Dispatcher.InvokeAsync(_window.ResetToDefaultPosition);
    }

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;

        _subscriptions.Add(host.EventBus.Subscribe<RecordingStartedEvent>(OnRecordingStarted));
        _subscriptions.Add(host.EventBus.Subscribe<RecordingStoppedEvent>(OnRecordingStopped));
        _subscriptions.Add(host.EventBus.Subscribe<PartialTranscriptionUpdateEvent>(OnPartialTranscriptionUpdate));
        _subscriptions.Add(host.EventBus.Subscribe<TranscriptionCompletedEvent>(OnTranscriptionCompleted));
        _subscriptions.Add(host.EventBus.Subscribe<TranscriptionFailedEvent>(OnTranscriptionFailed));

        host.Log(PluginLogLevel.Info, "Live Transcript plugin activated");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;

        if (_window is not null)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                _window.Close();
                _window = null;
            });
        }

        _host?.Log(PluginLogLevel.Info, "Live Transcript plugin deactivated");
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new LiveTranscriptSettingsView(this);

    private Task OnRecordingStarted(RecordingStartedEvent evt)
    {
        if (IsTerminalRecording(evt.RecordingId)) return Task.CompletedTask;

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;
        _activeRecordingId = evt.RecordingId;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (IsTerminalRecording(evt.RecordingId)) return;
            if (_activeRecordingId != evt.RecordingId) return;
            EnsureWindow();
            _window!.SetSavedPosition(WindowLeft, WindowTop);
            _window!.UpdateText("Listening...");
            _window.Show();
        });

        return Task.CompletedTask;
    }

    private Task OnRecordingStopped(RecordingStoppedEvent evt)
    {
        if (IsTerminalRecording(evt.RecordingId)) return Task.CompletedTask;
        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;
        // Keep window visible — it will be hidden after TranscriptionCompleted/Failed or timeout
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (IsTerminalRecording(evt.RecordingId)) return;
            if (evt.RecordingId != _activeRecordingId) return;
            if (_window is { IsVisible: true })
                _window.UpdateText(_window.CurrentText + "\nProcessing...");
        });

        return Task.CompletedTask;
    }

    private Task OnPartialTranscriptionUpdate(PartialTranscriptionUpdateEvent evt)
    {
        if (IsTerminalRecording(evt.RecordingId)) return Task.CompletedTask;
        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (IsTerminalRecording(evt.RecordingId)) return;
            if (evt.RecordingId != _activeRecordingId) return;
            EnsureWindow();
            _window!.UpdateText(evt.PartialText);
            if (!_window.IsVisible)
                _window.Show();
        });

        return Task.CompletedTask;
    }

    private Task OnTranscriptionCompleted(TranscriptionCompletedEvent evt)
    {
        MarkTerminalRecording(evt.RecordingId);
        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (evt.RecordingId != _activeRecordingId) return;
            if (_window is not null)
                _window.UpdateText(evt.Text);
        });

        var autoHideMilliseconds = AutoHideMilliseconds;
        if (autoHideMilliseconds <= 0)
            return Task.CompletedTask;

        _autoHideCts = new CancellationTokenSource();
        var token = _autoHideCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(autoHideMilliseconds, token);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (evt.RecordingId == _activeRecordingId)
                        _window?.Hide();
                });
            }
            catch (TaskCanceledException)
            {
                // Cancelled — a new recording started before auto-hide
            }
        }, token);

        return Task.CompletedTask;
    }

    private Task OnTranscriptionFailed(TranscriptionFailedEvent evt)
    {
        MarkTerminalRecording(evt.RecordingId);
        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (evt.RecordingId != _activeRecordingId) return;
            _window?.Hide();
        });
        return Task.CompletedTask;
    }

    private void EnsureWindow()
    {
        if (_window is null || !_window.IsLoaded)
        {
            _window = new LiveTranscriptWindow();
            _window.PositionChanged += SaveWindowPosition;
            _window.SetFontSize(FontSize);
            _window.SetWindowOpacity(Opacity);
            _window.SetSavedPosition(WindowLeft, WindowTop);
        }
    }

    private static int NormalizeAutoHideMilliseconds(int milliseconds) =>
        Math.Clamp(milliseconds, MinAutoHideMilliseconds, MaxAutoHideMilliseconds);

    private void MarkTerminalRecording(Guid? recordingId)
    {
        if (recordingId is not { } id) return;

        lock (_terminalRecordingLock)
        {
            if (!_terminalRecordingIds.Add(id)) return;

            _terminalRecordingOrder.Enqueue(id);
            while (_terminalRecordingOrder.Count > MaxTerminalRecordingIds)
            {
                _terminalRecordingIds.Remove(_terminalRecordingOrder.Dequeue());
            }
        }
    }

    private bool IsTerminalRecording(Guid? recordingId)
    {
        if (recordingId is not { } id) return false;

        lock (_terminalRecordingLock)
        {
            return _terminalRecordingIds.Contains(id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();

        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();

        if (_window is not null && Application.Current is not null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _window?.Close();
                _window = null;
            });
        }
    }
}
