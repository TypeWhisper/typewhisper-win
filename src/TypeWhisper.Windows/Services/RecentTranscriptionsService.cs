using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides recent transcriptions service behavior.
/// </summary>
public sealed class RecentTranscriptionsService
{
    private const int PaletteLimit = 12;

    private readonly IHistoryService _history;
    private readonly RecentTranscriptionStore _store;
    private readonly TextInsertionService _textInsertion;
    private readonly ISettingsService _settings;

    private RecentTranscriptionsPaletteWindow? _paletteWindow;

    /// <summary>
    /// Raised when feedback requested.
    /// </summary>
    public event Action<string, bool>? FeedbackRequested;

    /// <summary>
    /// Initializes a new instance of the RecentTranscriptionsService class.
    /// </summary>
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

    /// <summary>
    /// Records transcription.
    /// </summary>
    public void RecordTranscription(
        string id,
        string finalText,
        DateTime timestamp,
        string? appName,
        string? appProcessName) =>
        _store.RecordTranscription(id, finalText, timestamp, appName, appProcessName);

    /// <summary>
    /// Toggles palette.
    /// </summary>
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

    /// <summary>
    /// Copies last transcription to clipboard asynchronously.
    /// </summary>
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
        catch (InvalidOperationException ex)
        {
            ReportError(ex);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ReportError(ex);
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
                autoEnter: false,
                targetHwnd);
            FeedbackRequested?.Invoke(StatusTextFor(result), false);
        }
        catch (InvalidOperationException ex)
        {
            ReportError(ex);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ReportError(ex);
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

    private void ReportError(Exception ex) =>
        FeedbackRequested?.Invoke(Loc.Instance.GetString("Status.ErrorFormat", ex.Message), true);
}
