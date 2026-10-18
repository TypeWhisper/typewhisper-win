using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // The registry enforces policy for its roles. The local Parakeet path never touches the registry, so its
    // checks live here; its engine reports the sherpa-onnx package as its selection ID.
    private static ManagedProviderPolicy ProviderPolicy => ManagedProviderPolicy.From(WinUIManagedPolicy.Current);

    private static string? LocalTranscriptionRefusal() =>
        ProviderPolicy.TranscriptionRefusal(LocalTranscriptionPlugin.PluginId, LocalTranscriptionPlugin.PluginId, ContentLocality.Local);

    /// <summary>
    /// Why policy refuses the active transcription provider before audio is captured, or null. A provider whose
    /// package the policy unloaded has no role any more and is judged as a package.
    /// </summary>
    internal string? TranscriptionPolicyRefusal() => UsesRegistryProvider
        ? PluginRuntime.TranscriptionRefusal(RegistrySelectionId(_providerId))
            ?? (ActiveRegistryProvider is null ? PluginRuntime.PackageRefusal(RegistrySelectionId(_providerId)) : null)
        : LocalTranscriptionRefusal();

    // Dictionary terms are text: a cloud engine receives them as a prompt only where text processing is allowed.
    private IReadOnlyList<string>? PromptTermsFor(string selectionId, IReadOnlyList<string>? terms) =>
        terms is null || PluginRuntime.TranscriptionPromptRefusal(selectionId) is not null ? null : terms;

    private static void DenyLocalTranscriptionIfRefused()
    {
        if (LocalTranscriptionRefusal() is { } refused) throw new ManagedPolicyDeniedException("transcription", refused);
    }

    /// <summary>Why policy refuses downloading models or runtime assets, or null.</summary>
    internal static string? ModelDownloadRefusal() => ProviderPolicy.ModelDownloadRefusal();

    /// <summary>
    /// Re-applies the package policy to the plugin runtime and the local engine after a policy change. Waits for a
    /// running dictation to finish; its audio path refuses a newly refused provider on its own meanwhile.
    /// </summary>
    internal async Task ApplyPolicyChangeAsync()
    {
        if (_disposed) return;
        try
        {
            // Work already inside a refused local engine is cancelled first, before any registry unload is awaited;
            // the gate is then taken once the cancelled operation has let go of it. The full transcription rule
            // applies, so a provider allowlist that excludes the local engine cancels as well.
            if (Models.Enabled && !UsesRegistryProvider && TranscriptionPolicyRefusal() is not null)
            {
                if (_audio.IsRecording || _fileBusy || _phase == DictationPhase.Processing) RequestCancel();
                _training?.CancelDecode();
            }
            // Downloads that passed their check before the policy turned them off stop here; the runtime cancels its
            // own downloads in its pass.
            if (ModelDownloadRefusal() is not null)
            {
                Models.CancelDownload();
                ModelDownloadsRefused?.Invoke();
                await LocalLlmDownload.CancelAndDrainAsync();
            }
            await PluginRuntime.ReapplyPolicyAsync();
            await _gate.WaitAsync();
            try
            {
                // The local engine is judged only once its package exists; an absent package has nothing to refuse.
                if (_disposed || !Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) return;
                var previousError = LocalPluginError;
                if (Models.Enabled && ProviderPolicy.PluginRefusal(LocalTranscriptionPlugin.PluginId) is not null) await _livePreview.StopAsync();
                var changed = await Models.ReapplyPolicyAsync();
                if (_disposed) return;
                if (!changed)
                {
                    LocalPluginError = Models.Error ?? LocalPluginError;
                    if (LocalPluginError != previousError) Changed?.Invoke();
                    return;
                }
                var vocabularyError = await CtcVocabulary.SetEnabledAsync(Models.Enabled);
                LocalPluginError = Models.Error ?? vocabularyError;
                SetStatus(Models.Enabled
                    ? IsReady ? ModelReadyStatus() : Models.Error ?? Loc.T("Download a model in plugin settings, then select it in Dictation.")
                    : Models.Error ?? Loc.T("Local transcription plugin disabled"), DictationPhase.Idle);
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.plugins.reapply-failed", ex); }
    }
}
