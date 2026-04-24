using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.Services;

public sealed class RecentTranscriptionsService
{
    private const int PaletteLimit = 12;

    private readonly IHistoryService _history;
    private readonly RecentTranscriptionStore _store;
    private readonly TextInsertionService _textInsertion;
    private readonly ISettingsService _settings;

    private RecentTranscriptionsPaletteWindow? _paletteWindow;

    public event Action<string, bool>? FeedbackRequested;

    public RecentTranscriptionsService(
        IHistoryService history,
        RecentTranscriptionStore store,
        TextInsertionService textInsertion,
        ISettingsService settings)
    {
        _history = history;
        _store = store;
        _textInsertion = textInsertion;
        _settings = settings;
    }

    public void RecordTranscription(
        string id,
        string finalText,
        DateTime timestamp,
        string? appName,
        string? appProcessName) =>
        _store.RecordTranscription(id, finalText, timestamp, appName, appProcessName);

    public void TogglePalette()
    {
        if (_paletteWindow is { } existingWindow)
        {
            existingWindow.RequestClose();
            return;
        }

        var entries = _store.MergedEntries(_history.Records, PaletteLimit);
        if (entries.Count == 0)
        {
            FeedbackRequested?.Invoke(Loc.Instance["RecentTranscriptions.Empty"], false);
            return;
        }

        var targetHwnd = NativeMethods.GetForegroundWindow();
        var viewModel = new RecentTranscriptionsPaletteViewModel(
            entries,
            item => _ = InsertEntryAsync(item.Entry, targetHwnd));
        var window = new RecentTranscriptionsPaletteWindow(viewModel);
        _paletteWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_paletteWindow, window))
                _paletteWindow = null;
        };

        window.Show();
        window.Activate();
    }

    public async Task CopyLastTranscriptionToClipboardAsync()
    {
        var entry = _store.LatestEntry(_history.Records);
        if (entry is null)
        {
            FeedbackRequested?.Invoke(Loc.Instance["RecentTranscriptions.Empty"], false);
            return;
        }

        try
        {
            var result = await _textInsertion.InsertTextAsync(entry.FinalText, autoPaste: false);
            FeedbackRequested?.Invoke(StatusTextFor(result), false);
        }
        catch (Exception ex)
        {
            FeedbackRequested?.Invoke(Loc.Instance.GetString("Status.ErrorFormat", ex.Message), true);
        }
    }

    private async Task InsertEntryAsync(RecentTranscriptionEntry entry, IntPtr targetHwnd)
    {
        try
        {
            if (targetHwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
                await Task.Delay(100);
            }

            var result = await _textInsertion.InsertTextAsync(
                entry.FinalText,
                _settings.Current.AutoPaste,
                autoEnter: false);
            FeedbackRequested?.Invoke(StatusTextFor(result), false);
        }
        catch (Exception ex)
        {
            FeedbackRequested?.Invoke(Loc.Instance.GetString("Status.ErrorFormat", ex.Message), true);
        }
    }

    private static string StatusTextFor(InsertionResult result) =>
        result switch
        {
            InsertionResult.Pasted => Loc.Instance["Status.Pasted"],
            InsertionResult.CopiedToClipboard => Loc.Instance["Status.Clipboard"],
            InsertionResult.NoText => Loc.Instance["RecentTranscriptions.Empty"],
            _ => Loc.Instance["Status.Done"]
        };
}
