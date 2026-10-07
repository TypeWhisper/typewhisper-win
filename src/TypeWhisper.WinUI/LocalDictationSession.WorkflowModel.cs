namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // Restores the selected transcription model after a workflow recording; owned by the dictation gate.
    private IAsyncDisposable? _workflowModelOverride;
    // The language selected in Dictation before a workflow model replaced the provider.
    private string? _languageBeforeWorkflowModel;

    // Whether the start has to load a workflow model other than the loaded one.
    private bool SwitchesWorkflowModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && !(IsReady && model == _providerId + ":" + ActiveModelId);

    // Like any loading model, a workflow model must not cost the first words: the microphone opens
    // before the load and the start adopts that capture. Without it, the overlay shows the load.
    private bool BeginWorkflowModelCapture()
    {
        SetStatus(Loc.T("Loading the workflow's transcription model…"), DictationPhase.LoadingModel);
        if (BeginEarlyCapture()) return true;
        _showModelLoadingForDictation = true;
        Changed?.Invoke();
        return false;
    }

    // Loads a dictation workflow's transcription model without saving it as the selection.
    private async Task<bool> RejectWorkflowModelAsync(string? model, Action<string>? rejected)
    {
        _languageBeforeWorkflowModel = null;
        if (string.IsNullOrWhiteSpace(model)) return false;
        var language = Language;
        try
        {
            _workflowModelOverride = await BeginApiModelOverrideAsync(null, model, awaitDownload: false, CancellationToken.None);
            if (_workflowModelOverride is not null) _languageBeforeWorkflowModel = language;
            AppDiagnostics.Write($"dictation.workflow-model engine={ActiveEngineId} model={ActiveModelId} switched={_workflowModelOverride is not null}");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write("dictation.workflow-model.failed " + ex.GetType().Name);
            var message = Loc.T("This workflow's transcription model could not be loaded. Download or set it up in Dictation, or choose another model in the workflow.");
            rejected?.Invoke(message);
            SetStatus(message, DictationPhase.Error);
            return true;
        }
    }

    // "Use global setting" keeps the language selected in Dictation when the workflow model accepts it,
    // and otherwise that model's own setting.
    private string InheritedLanguage() => _languageBeforeWorkflowModel is { } before && before != "auto"
        && LanguageChoices.Contains(before, StringComparer.OrdinalIgnoreCase) ? before : Language;

    // A capture that ended outside the start and stop paths, such as a lost microphone, still restores the model.
    private async Task RestoreAfterCaptureLossAsync()
    {
        if (_workflowModelOverride is null) return;
        await _gate.WaitAsync();
        try { if (!_disposed && !_audio.IsRecording) await RestoreWorkflowModelAsync(); }
        finally { _gate.Release(); }
    }

    // Runs once the recording has finished or failed to start, before the gate admits the next dictation.
    private async Task RestoreWorkflowModelAsync()
    {
        if (Interlocked.Exchange(ref _workflowModelOverride, null) is not { } scope) return;
        try { await scope.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write("dictation.workflow-model.restore-failed " + ex.GetType().Name);
            SetStatus(Loc.T("Your selected transcription model could not be restored after the workflow. Select it again in Dictation."), DictationPhase.Error);
        }
    }
}
