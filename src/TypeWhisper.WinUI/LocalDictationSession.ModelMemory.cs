using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal ModelMemoryPreferencesStore ModelMemoryPreferences { get; } = new(WinUIProfile.DataPath("model-memory.json"));
    private ModelIdleUnloadPolicy? _modelIdlePolicy;
    // Shared by transcription workers and local text models. Releases wait while dictation, file
    // transcription, the recorder or a workflow may still use a model.
    private ModelIdleUnloadPolicy ModelIdlePolicy => _modelIdlePolicy ??=
        new(ModelMemoryPreferences.AutoUnloadSeconds) { CanUnload = () => CanStartSessionOperation };

    internal string? SelectModelAutoUnload(int seconds)
    {
        var error = ModelMemoryPreferences.Save(seconds);
        if (error is null) ModelIdlePolicy.SetSeconds(seconds);
        Changed?.Invoke();
        return error;
    }

    // A model released after inactivity loads while the user speaks instead of at the final transcription.
    private void PrepareTranscriptionModel()
    {
        if (UsesRegistryProvider) _ = PrepareRegistryTranscriptionAsync(RegistrySelectionId(_providerId));
        else Models.PrepareForDictation();
    }

    // Failures surface on the transcription itself.
    private async Task PrepareRegistryTranscriptionAsync(string selectionId)
    {
        try { await PluginRuntime.PrepareTranscriptionWorkerAsync(selectionId); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("dictation.model-prepare.failed " + ex.GetType().Name); }
    }
}
