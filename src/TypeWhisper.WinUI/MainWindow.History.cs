using Microsoft.UI.Windowing;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// History opens in its own window, as on macOS.
public sealed partial class MainWindow
{
    private readonly TypeWhisper.Core.Services.HistoryService? _historyService;
    private HistoryWindow? _historyWindow;

    private bool CanPlayHistoryAudio() => !_closing && !_profileRestoreClosing && _dictation.CanChangeProvider && !_dictation.Models.Busy
        && _dictationInput?.IsRecordingOrStarting != true && _workflowTask is not { IsCompleted: false };

    internal void ShowHistoryFromTray() => OpenHistory();

    // Opens History, optionally showing only one local source (dictation or recording).
    private void OpenHistory(string? source = null)
    {
        if (_closing || _profileRestoreClosing || _historyService is null) return;
        if (_historyWindow is null)
        {
            var actions = new HistoryActions(_historyService);
            actions.ExplicitlyDeleted += WinUICloudSync.RecordHistoryDeletions;
            var window = _historyWindow = new HistoryWindow(new HistoryReader(_historyService), actions, _historyService)
            {
                CanPlayAudio = CanPlayHistoryAudio,
                PrepareAudioPlayback = _dictation.SpokenFeedback.CancelAndDrainAsync,
                LocalDeviceId = () => WinUICloudSync.HistoryDeviceId,
                Devices = () => WinUICloudSync.HistoryDevices
            };
            void DevicesChanged() => window.DispatcherQueue.TryEnqueue(window.RenderDevices);
            WinUICloudSync.HistoryDevicesChanged += DevicesChanged;
            window.Closed += (_, _) =>
            {
                WinUICloudSync.HistoryDevicesChanged -= DevicesChanged;
                if (ReferenceEquals(_historyWindow, window)) _historyWindow = null;
            };
            window.ShowOn(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
        }
        else _historyWindow.BringToFront();
        if (source is not null) _historyWindow.ShowSource(source);
    }
}
