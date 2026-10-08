using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal ModelMemoryPreferencesStore ModelMemoryPreferences => _plugins.ModelMemoryPreferences;
    // Shared by transcription workers and local text models. Releases wait while dictation, file
    // transcription, the recorder or a workflow may still use a model.
    private ModelIdleUnloadPolicy ModelIdlePolicy => _plugins.IdlePolicy;

    internal string? SelectModelAutoUnload(int seconds)
    {
        var error = ModelMemoryPreferences.Save(seconds);
        if (error is null) ModelIdlePolicy.SetSeconds(seconds);
        Changed?.Invoke();
        return error;
    }

    // Parakeet TDT dictations are rescored against the dictionary right after the decode (#577).
    private bool RescoresWithCtc => _taskAtStart == TranscriptionTask.Transcribe && !UsesRegistryProvider
        && ParakeetModels.IsParakeetTdt(Models.ActiveModelId);

    // A model released after inactivity loads while the user speaks instead of at the final transcription.
    // The rescoring model follows the same policy and is needed right after the decode.
    private void PrepareTranscriptionModel()
    {
        if (UsesRegistryProvider) _ = PrepareRegistryTranscriptionAsync(RegistrySelectionId(_providerId));
        else Models.PrepareForDictation();
        if (RescoresWithCtc) CtcVocabulary.PrepareForDictation();
    }

    // Failures surface on the transcription itself.
    private async Task PrepareRegistryTranscriptionAsync(string selectionId)
    {
        try { await PluginRuntime.PrepareTranscriptionWorkerAsync(selectionId); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("dictation.model-prepare.failed " + ex.GetType().Name); }
    }
}
