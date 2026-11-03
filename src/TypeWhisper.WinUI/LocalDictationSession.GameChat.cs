using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // Set when the current dictation's game add-on shows it in the game; cleared when the next capture starts.
    private GameIndicatorSignals? _gameIndicator;
    // Fires when the game's bar times out during a long recording or transcription, so TypeWhisper's indicator returns.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gameIndicatorTimer;
    private bool _gameCaptured;
    // Whether this dictation's text went into the game's chat; a copy or a workflow action completes without it.
    private bool _gameChatReached;
    private Workflow? _gameTranslationAtStart;

    /// <summary>Whether the game add-on shows the current dictation, so TypeWhisper's own indicator stays hidden. A
    /// signal that did not reach the game, such as while Alt is held, keeps TypeWhisper's indicator, and so does a game
    /// that is no longer in front: a minimized game cannot show that the microphone is still open.</summary>
    internal bool ShowsGameIndicator => _gameIndicator is { Shown: true } && NativeMethods.GetForegroundWindow() == _target;

    // Once per dictation, after capture began. The add-on's page in the game decides sending, the cast bar and
    // translation; the game saves it on /reload or logout, and TypeWhisper reads it from there.
    private void CaptureGameAtStart()
    {
        if (_gameCaptured) return;
        _gameCaptured = true;
        _gameChatReached = false;
        _chatAtStart = null;
        _gameIndicator = null;
        _gameTranslationAtStart = null;
        if (_setupOutputAtStart is not null || GamePreferences.Current.GameFor(_targetApp) is null) return;
        var versionFolder = Path.GetDirectoryName(ProcessImagePath(_targetProcessId));
        var addOn = WowAddOnSettings.Read(versionFolder);
        var installed = WowAddOn.IsInstalledIn(versionFolder);
        _chatAtStart = GamePreferences.Current.ChatFor(_targetApp, addOn, installed);
        _gameIndicator = CreateGameIndicator(GamePreferences.Current.IndicatorFor(_targetApp, addOn, installed));
        _gameTranslationAtStart = addOn.TranslationWorkflow();
        AppDiagnostics.Write($"dictation.game-chat open={_chatAtStart?.OpenKey} submit={addOn.Send} indicator={_gameIndicator is not null} translate={addOn.TranslateTo is not null}");
        // The add-on page names the translating LLM after the next /reload.
        var llm = GameTranslationLlm();
        WowAddOn.WriteStatus(versionFolder, llm.Name, llm.Ready);
    }

    /// <summary>The shared workflow LLM that translates game chat, by display name, and whether it can run now. Without
    /// it the add-on's translation is skipped and messages go out as dictated: a failed translation would open review
    /// over the game for every message.</summary>
    internal (string? Name, bool Ready) GameTranslationLlm()
    {
        try
        {
            var behavior = WorkflowDefaults.Resolve(new WowAddOnSettings(TranslateTo: "English").TranslationWorkflow()!).Behavior;
            if (behavior.ProviderOverride is not { Length: > 0 } provider || provider == WorkflowLlmDefaults.Inherit
                || behavior.ModelOverride is not { Length: > 0 } model) return (null, false);
            var names = LlmDisplayNames(provider, model);
            var ready = LlmProviders.Any(item => item.SelectionId == provider && item.Ready && item.Models.Any(candidate => candidate.Id == model));
            return ($"{names.Provider} · {names.Model}", ready);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, false); }
    }

    private bool TranslationLlmReady(Workflow translation)
    {
        try
        {
            var behavior = WorkflowDefaults.Resolve(translation).Behavior;
            return behavior.ProviderOverride is { Length: > 0 } provider && behavior.ModelOverride is { Length: > 0 } model
                && LlmProviders.Any(item => item.SelectionId == provider && item.Ready && item.Models.Any(candidate => candidate.Id == model));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    // The executable's path with the least access a process grants, so the game is never opened for reading memory.
    private static string? ProcessImagePath(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    private void ObserveGameIndicator()
    {
        if (_gameIndicator is not { } indicator) return;
        indicator.Observe(OverlayState.Phase, _audio.IsRecording, _gameChatReached);
        if (indicator.ExpiresAt is not { } expires || _gameIndicatorTimer is not { } timer) return;
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, (expires - DateTimeOffset.UtcNow).TotalMilliseconds) + 250);
        timer.Start();
    }

    private void CreateGameIndicatorTimer(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _gameIndicatorTimer = dispatcher.CreateTimer();
        _gameIndicatorTimer.IsRepeating = false;
        _gameIndicatorTimer.Tick += (_, _) => { if (!_disposed) Changed?.Invoke(); };
    }

    private GameIndicatorSignals? CreateGameIndicator(GameIndicatorChords? chords)
    {
        if (chords is null) return null;
        var target = _target;
        return new(chords, chord =>
        {
            var sent = WindowsChatDeliveryPlatform.Signal(target, chord);
            var signal = chord == chords.Recording ? "Recording" : chord == chords.Transcribing ? "Transcribing" : "Stopped";
            AppDiagnostics.Write($"game.indicator signal={signal} sent={sent}");
            return sent;
        });
    }

    // Partial delivery reports failure, so the review and the clipboard keep the full text for the messages that were not sent.
    private async Task<bool> DeliverToChatAsync(string text, ChatDeliveryOptions chat)
    {
        var platform = new WindowsChatDeliveryPlatform(_target, async part => (await _inserter.InsertAsync(part, _target, () =>
            !_disposed && !_operationCancellation.Token.IsCancellationRequested
            && _outputAtStart.RestrictedBy(OutputPreferences.Current).AutoPaste,
            preserveClipboard: () => _outputAtStart.RestrictedBy(OutputPreferences.Current).PreserveClipboard)).Inserted, chat.SettleMilliseconds);
        var result = await ChatDeliveryOperation.RunAsync(platform, text, chat, _operationCancellation.Token);
        _gameChatReached = result.Sent > 0;
        AppDiagnostics.Write(result.Completed
            ? $"chat.sent messages={result.Sent}"
            : $"chat.stopped stage={result.FailedAt} messages={result.Sent} total={result.Total}");
        return result.Completed && result.Sent > 0;
    }
}
