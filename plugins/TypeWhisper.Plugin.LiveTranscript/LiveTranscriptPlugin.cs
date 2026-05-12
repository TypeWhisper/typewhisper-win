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
    private IPluginHostServices? _host;
    private LiveTranscriptWindow? _window;
    private readonly List<IDisposable> _subscriptions = [];
    private CancellationTokenSource? _autoHideCts;
    private bool _disposed;

    public string PluginId => "com.typewhisper.live-transcript";
    public string PluginName => "Live Transcript";
    public string PluginVersion => "1.0.0";

    public int FontSize
    {
        get => _host?.GetSetting<int?>("fontSize") ?? 16;
        set
        {
            _host?.SetSetting("fontSize", value);
            if (_window is not null)
                Application.Current?.Dispatcher.InvokeAsync(() => _window.SetFontSize(value));
        }
    }

    public double Opacity
    {
        get => _host?.GetSetting<double?>("opacity") ?? 0.85;
        set
        {
            _host?.SetSetting("opacity", value);
            if (_window is not null)
                Application.Current?.Dispatcher.InvokeAsync(() => _window.SetWindowOpacity(value));
        }
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
        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            EnsureWindow();
            _window!.UpdateText("Listening...");
            _window.Show();
        });

        return Task.CompletedTask;
    }

    private Task OnRecordingStopped(RecordingStoppedEvent evt)
    {
        // Keep window visible — it will be hidden after TranscriptionCompleted or timeout
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_window is { IsVisible: true })
                _window.UpdateText(_window.CurrentText + "\nProcessing...");
        });

        return Task.CompletedTask;
    }

    private Task OnPartialTranscriptionUpdate(PartialTranscriptionUpdateEvent evt)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            EnsureWindow();
            _window!.UpdateText(evt.PartialText);
            if (!_window.IsVisible)
                _window.Show();
        });

        return Task.CompletedTask;
    }

    private Task OnTranscriptionCompleted(TranscriptionCompletedEvent evt)
    {
        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = new CancellationTokenSource();
        var token = _autoHideCts.Token;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_window is not null)
                _window.UpdateText(evt.Text);
        });

        // Auto-hide after 3 seconds
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, token);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
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
        _autoHideCts?.Cancel();
        _autoHideCts?.Dispose();
        _autoHideCts = null;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _window?.Hide();
        });

        return Task.CompletedTask;
    }

    private void EnsureWindow()
    {
        if (_window is null || !_window.IsLoaded)
        {
            _window = new LiveTranscriptWindow();
            _window.SetFontSize(FontSize);
            _window.SetWindowOpacity(Opacity);
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
