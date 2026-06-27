using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.LiveTranscript;

/// <summary>
/// Provides live transcript plugin behavior.
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
    private Guid? _pendingTerminalRecordingId;
    private bool _activeRecordingEnded;

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.live-transcript";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Live Transcript";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.2";

    /// <summary>
    /// Gets whether uses host appearance.
    /// </summary>
    public bool UsesHostAppearance => _host is ILivePreviewAppearanceProvider;

    private bool LivePreviewEnabled =>
        (_host as ILivePreviewAppearanceProvider)?.LiveTranscriptionPreviewEnabled ?? true;

    /// <summary>
    /// Gets the font size.
    /// </summary>
    public double FontSize
    {
        get => (_host as ILivePreviewAppearanceProvider)?.LiveTranscriptionFontSize
            ?? _host?.GetSetting<double?>(FontSizeSettingName)
            ?? 16d;
        set
        {
            if (UsesHostAppearance)
                return;

            _host?.SetSetting(FontSizeSettingName, value);
            if (_window is not null)
                DispatchToUi(() => _window.SetFontSize(FontSize));
        }
    }

    /// <summary>
    /// Gets the opacity.
    /// </summary>
    public double Opacity
    {
        get => _host?.GetSetting<double?>(OpacitySettingName) ?? 0.85;
        set
        {
            _host?.SetSetting(OpacitySettingName, value);
            if (_window is not null)
                DispatchToUi(() => _window.SetWindowOpacity(value));
        }
    }

    /// <summary>
    /// Gets the auto hide milliseconds.
    /// </summary>
    public int AutoHideMilliseconds
    {
        get => (_host as ILivePreviewAppearanceProvider)?.PreviewBubbleAutoHideMilliseconds
            ?? NormalizeAutoHideMilliseconds(
                _host?.GetSetting<int?>(AutoHideMillisecondsSettingName) ?? DefaultAutoHideMilliseconds);
        set
        {
            if (UsesHostAppearance)
                return;

            _host?.SetSetting(AutoHideMillisecondsSettingName, NormalizeAutoHideMilliseconds(value));
        }
    }

    private double? WindowLeft => _host?.GetSetting<double?>(WindowLeftSettingName);
    private double? WindowTop => _host?.GetSetting<double?>(WindowTopSettingName);

    /// <summary>
    /// Saves window position.
    /// </summary>
    public void SaveWindowPosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
            return;

        _host?.SetSetting(WindowLeftSettingName, left);
        _host?.SetSetting(WindowTopSettingName, top);
    }

    /// <summary>
    /// Performs reset window position.
    /// </summary>
    public void ResetWindowPosition()
    {
        _host?.SetSetting<double?>(WindowLeftSettingName, null);
        _host?.SetSetting<double?>(WindowTopSettingName, null);

        if (_window is not null)
            DispatchToUi(_window.ResetToDefaultPosition);
    }

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;

        _subscriptions.Add(host.EventBus.Subscribe<RecordingStartedEvent>(OnRecordingStarted));
        _subscriptions.Add(host.EventBus.Subscribe<RecordingStoppedEvent>(OnRecordingStopped));
        _subscriptions.Add(host.EventBus.Subscribe<PartialTranscriptionUpdateEvent>(OnPartialTranscriptionUpdate));
        _subscriptions.Add(host.EventBus.Subscribe<TranscriptionCompletedEvent>(OnTranscriptionCompleted));
        _subscriptions.Add(host.EventBus.Subscribe<TranscriptionFailedEvent>(OnTranscriptionFailed));
        _subscriptions.Add(host.EventBus.Subscribe<TextInsertedEvent>(OnTextInserted));

        host.Log(PluginLogLevel.Info, "Live Transcript plugin activated");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
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
            DispatchToUi(() =>
            {
                _window.Close();
                _window = null;
            });
        }

        _host?.Log(PluginLogLevel.Info, "Live Transcript plugin deactivated");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new LiveTranscriptSettingsView(this);

    private Task OnRecordingStarted(RecordingStartedEvent evt)
    {
        if (IsTerminalRecording(evt.RecordingId)) return Task.CompletedTask;
        if (SuppressLivePreviewIfDisabled(evt.RecordingId)) return Task.CompletedTask;

        CancelAutoHide();
        _activeRecordingEnded = false;
        _activeRecordingId = evt.RecordingId;

        DispatchToUi(() =>
        {
            if (IsTerminalRecording(evt.RecordingId)) return;
            if (_activeRecordingId != evt.RecordingId) return;
            if (_activeRecordingEnded) return;
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
        if (SuppressLivePreviewIfDisabled(evt.RecordingId)) return Task.CompletedTask;
        if (_activeRecordingEnded) return Task.CompletedTask;
        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;
        _pendingTerminalRecordingId = evt.RecordingId;

        // Keep window visible while final processing and text insertion complete.
        DispatchToUi(() =>
        {
            if (IsTerminalRecording(evt.RecordingId)) return;
            if (_activeRecordingEnded) return;
            if (evt.RecordingId != _activeRecordingId) return;
            if (_window is { IsVisible: true })
                _window.UpdateText(_window.CurrentText + "\nProcessing...");
        });

        return Task.CompletedTask;
    }

    private Task OnPartialTranscriptionUpdate(PartialTranscriptionUpdateEvent evt)
    {
        if (IsTerminalRecording(evt.RecordingId)) return Task.CompletedTask;
        if (SuppressLivePreviewIfDisabled(evt.RecordingId)) return Task.CompletedTask;
        if (_activeRecordingEnded) return Task.CompletedTask;
        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;
        DispatchToUi(() =>
        {
            if (IsTerminalRecording(evt.RecordingId)) return;
            if (_activeRecordingEnded) return;
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
        if (SuppressLivePreviewIfDisabled(evt.RecordingId)) return Task.CompletedTask;

        MarkTerminalRecording(evt.RecordingId);
        ClearPendingTerminalRecording(evt.RecordingId);
        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;
        _activeRecordingEnded = true;

        CancelAutoHide();

        DispatchToUi(() =>
        {
            if (evt.RecordingId != _activeRecordingId) return;
            if (_window is not null)
                _window.UpdateText(evt.Text);
        });

        return Task.CompletedTask;
    }

    private Task OnTextInserted(TextInsertedEvent evt)
    {
        if (SuppressLivePreviewIfDisabled(evt.RecordingId)) return Task.CompletedTask;

        if (evt.RecordingId != _activeRecordingId) return Task.CompletedTask;

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;

        DispatchToUi(() =>
        {
            if (evt.RecordingId != _activeRecordingId) return;
            if (_window is not null)
                _window.UpdateText(evt.Text);
        });

        var autoHideMilliseconds = AutoHideMilliseconds;
        if (autoHideMilliseconds <= 0)
        {
            if (UsesHostAppearance)
            {
                DispatchToUi(() =>
                {
                    if (evt.RecordingId == _activeRecordingId)
                        _window?.Hide();
                });
            }

            return Task.CompletedTask;
        }

        _autoHideCts = new CancellationTokenSource();
        var token = _autoHideCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(autoHideMilliseconds, token);
                DispatchToUi(() =>
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
        if (SuppressLivePreviewIfDisabled(evt.RecordingId)) return Task.CompletedTask;

        var terminalRecordingId = evt.RecordingId ?? _pendingTerminalRecordingId;
        MarkTerminalRecording(terminalRecordingId);
        ClearPendingTerminalRecording(terminalRecordingId);

        var appliesToActiveRecording = terminalRecordingId == _activeRecordingId
            || (terminalRecordingId is null && _activeRecordingId is null);
        if (!appliesToActiveRecording) return Task.CompletedTask;

        _activeRecordingEnded = true;

        CancelAutoHide();

        DispatchToUi(() =>
        {
            if (terminalRecordingId != _activeRecordingId
                && !(terminalRecordingId is null && _activeRecordingId is null))
            {
                return;
            }

            _window?.Hide();
        });
        return Task.CompletedTask;
    }

    private bool SuppressLivePreviewIfDisabled(Guid? recordingId)
    {
        if (LivePreviewEnabled)
            return false;

        CancelAutoHide();
        MarkTerminalRecording(recordingId);
        ClearPendingTerminalRecording(recordingId);
        _activeRecordingEnded = true;

        if (recordingId == _activeRecordingId || _activeRecordingId is null)
            _activeRecordingId = null;

        DispatchToUi(() => _window?.Hide());
        return true;
    }

    private void EnsureWindow()
    {
        if (_window is null || !_window.IsLoaded)
        {
            _window = new LiveTranscriptWindow();
            _window.PositionChanged += SaveWindowPosition;
        }

        _window.SetFontSize(FontSize);
        _window.SetWindowOpacity(Opacity);
        _window.SetSavedPosition(WindowLeft, WindowTop);
    }

    private static int NormalizeAutoHideMilliseconds(int milliseconds) =>
        Math.Clamp(milliseconds, MinAutoHideMilliseconds, MaxAutoHideMilliseconds);

    private void DispatchToUi(Action action)
    {
        var dispatcher = ResolveDispatcher();
        if (dispatcher is null)
            return;

        dispatcher.InvokeAsync(action);
    }

    private Dispatcher? ResolveDispatcher()
    {
        if (_window?.Dispatcher is { } windowDispatcher && IsUsable(windowDispatcher))
            return windowDispatcher;

        if (Application.Current?.Dispatcher is { } appDispatcher && IsUsable(appDispatcher))
            return appDispatcher;

        return Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Dispatcher.CurrentDispatcher
            : null;
    }

    private static bool IsUsable(Dispatcher dispatcher) =>
        !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished;

    private void CancelAutoHide()
    {
        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;
    }

    private void ClearPendingTerminalRecording(Guid? recordingId)
    {
        if (recordingId == _pendingTerminalRecordingId)
            _pendingTerminalRecordingId = null;
    }

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

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();

        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();

        if (_window is not null)
        {
            DispatchToUi(() =>
            {
                _window?.Close();
                _window = null;
            });
        }
    }
}
