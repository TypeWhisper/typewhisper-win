using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // The registry provider's own model selection before an enforced pair replaced it. The dictation provider and the
    // local engine's model need no retention: the enforced pair is applied without writing their stores.
    private readonly SelectionPolicyStore _selectionPolicy = new(WinUIProfile.DataPath("selection-policy.json"));
    private bool _selectionPolicyApplying;
    private bool _selectionPolicyRerun;

    /// <summary>The transcription pair policy enforces, or null.</summary>
    internal static ManagedSelection? EnforcedTranscription => ProviderPolicy.EnforcedTranscription;

    /// <summary>Whether the dictation provider and model are fixed by policy or wait for a readable one.</summary>
    internal static bool TranscriptionSelectionLocked => EnforcedTranscription is not null || ProviderPolicy.TranscriptionSelectionBlocked;

    /// <summary>The model policy enforces for the active provider, or null; shown selected even before it is downloaded.</summary>
    internal string? EnforcedModelId => EnforcedTranscription is { } pair && SessionProviderId(pair.ProviderId) == _providerId ? pair.ModelId : null;

    /// <summary>What the locked dictation row says: the required pair, or why it cannot be used yet.</summary>
    internal string? SelectionLockReason()
    {
        if (ProviderPolicy.TranscriptionSelectionBlocked)
            return Loc.T("Your organization's policy could not be read. This operation waits until it is readable.");
        if (EnforcedTranscription is not { } pair) return null;
        var provider = DictationProviders.FirstOrDefault(item => item.Id == SessionProviderId(pair.ProviderId));
        var model = provider?.Models.FirstOrDefault(item => string.Equals(item.Id, pair.ModelId, StringComparison.OrdinalIgnoreCase));
        return Loc.T("Managed by your organization") + ": " + (provider?.Name ?? pair.ProviderId) + " · " + (model?.Name ?? pair.ModelId)
            + (TranscriptionPolicyRefusal() is { } refused ? " · " + refused : "");
    }

    /// <summary>Why a user or API selection may not become the dictation selection, or null.</summary>
    internal string? SelectionRefusal(string providerId, string modelId)
    {
        if (ProviderPolicy.TranscriptionSelectionBlocked)
            return Loc.T("Your organization's policy could not be read. This operation waits until it is readable.");
        if (EnforcedTranscription is not { } pair) return null;
        return SessionProviderId(pair.ProviderId) != providerId
            ? Loc.T("Your organization requires the transcription provider {0}.", pair.ProviderId)
            : !pair.IsModel(modelId) ? Loc.T("Your organization requires the transcription model {0}.", pair.ModelId) : null;
    }

    // A dictation workflow's own model is ignored while policy fixes the selection: the enforced pair wins.
    private static string? WorkflowTranscriptionModel(AutomaticWorkflowSnapshot? workflow) =>
        TranscriptionSelectionLocked ? null : workflow?.TranscriptionModel;

    // The provider at startup: the enforced pair, then the user's saved choice, then a recommendation, then local.
    private string StartupProviderId() => SessionProviderId(
        EnforcedTranscription?.ProviderId ?? _selection.GetSetting<string>("Provider") ?? ProviderPolicy.RecommendedTranscription?.ProviderId ?? "local");

    // The local engine reads the pair when it enables itself, so the enforced model loads once instead of twice.
    internal static (string? Enforced, string? Recommended) LocalEnginePolicyModels()
    {
        var policy = ProviderPolicy;
        return (policy.EnforcedTranscription is { } pair && pair.IsProvider(LocalTranscriptionPlugin.PluginId) ? pair.ModelId : null,
            policy.RecommendedTranscription is { } recommended && recommended.IsProvider(LocalTranscriptionPlugin.PluginId) ? recommended.ModelId : null);
    }

    private static WorkflowLlmSelection? EnforcedLlmSelection() =>
        ProviderPolicy.EnforcedLlm is { } pair ? new(pair.ProviderId, pair.ModelId) : null;

    private static WorkflowLlmSelection? RecommendedLlmSelection() =>
        ProviderPolicy.RecommendedLlm is { } pair ? new(pair.ProviderId, pair.ModelId) : null;

    /// <summary>Applies the enforced transcription pair after a policy change; serialized behind the dictation gate.</summary>
    internal async Task ApplySelectionPolicyAsync()
    {
        if (_disposed) return;
        if (_selectionPolicyApplying) { _selectionPolicyRerun = true; return; }
        _selectionPolicyApplying = true;
        try
        {
            do
            {
                _selectionPolicyRerun = false;
                await _gate.WaitAsync();
                try { if (!_disposed) await ApplySelectionPolicyCoreAsync(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.selection.apply-failed", ex); }
                finally { _gate.Release(); }
            } while (_selectionPolicyRerun && !_disposed);
        }
        finally { _selectionPolicyApplying = false; }
    }

    // Runs with the gate held. Never writes the dictation provider store or the local engine's selection; the registry
    // provider's own selection is changed and retained because the SDK has no transient selection.
    private async Task ApplySelectionPolicyCoreAsync()
    {
        var policy = ProviderPolicy;
        var before = (_providerId, ActiveModelId);
        try
        {
            if (policy.TranscriptionSelectionBlocked) return;
            if (policy.EnforcedTranscription is { } pair)
            {
                await _livePreview.StopAsync();
                _providerId = SessionProviderId(pair.ProviderId);
                if (!UsesRegistryProvider)
                {
                    if (Models.Enabled && !Models.Busy && Models.ActiveModelId != pair.ModelId
                        && Models.Models.Any(item => string.Equals(item.Model.Id, pair.ModelId, StringComparison.OrdinalIgnoreCase) && item.Downloaded))
                        await Models.ActivateAsync(pair.ModelId, CancellationToken.None, persistSelection: false);
                }
                else if (ActiveRegistryProvider is { } provider && !pair.IsModel(provider.SelectedModelId ?? ""))
                {
                    var target = provider.ModelStates.FirstOrDefault(item => pair.IsModel(item.ModelId));
                    if (target is not null && (!target.SupportsDownload || target.Downloaded))
                    {
                        if (_selectionPolicy.Current.RetainedProviderId is null && provider.SelectedModelId is not null)
                            _selectionPolicy.Save(new(provider.SelectionId, provider.SelectedModelId));
                        await PluginRuntime.SelectModelAsync(target, CancellationToken.None);
                        await PluginRuntime.RefreshCapabilitiesAsync();
                    }
                }
                return;
            }
            // Confirmed removal: the user's saved provider applies again and a retained registry model is selected once.
            if (_selectionPolicy.Current is { RetainedProviderId: { } retainedProvider, RetainedModelId: { } retainedModel })
            {
                var provider = PluginRuntime.TranscriptionProviders.FirstOrDefault(item => item.SelectionId == retainedProvider);
                var target = provider?.ModelStates.FirstOrDefault(item => item.ModelId == retainedModel);
                if (provider is not null && provider.SelectedModelId != retainedModel && target is not null && (!target.SupportsDownload || target.Downloaded))
                {
                    await _livePreview.StopAsync();
                    await PluginRuntime.SelectModelAsync(target, CancellationToken.None);
                    await PluginRuntime.RefreshCapabilitiesAsync();
                }
                _selectionPolicy.Save(new());
            }
            var chosen = _selection.GetSetting<string>("Provider");
            var saved = SessionProviderId(chosen ?? policy.RecommendedTranscription?.ProviderId ?? "local");
            if (_providerId != saved) { await _livePreview.StopAsync(); _providerId = saved; }
            // A recommended registry model is the initial selection of a profile without any choice, once it is downloaded.
            if (chosen is null && policy.RecommendedTranscription is { } recommended && UsesRegistryProvider
                && ActiveRegistryProvider is { SelectedModelId: null } fresh && recommended.IsProvider(fresh.SelectionId)
                && fresh.ModelStates.FirstOrDefault(item => recommended.IsModel(item.ModelId)) is { } initial && (!initial.SupportsDownload || initial.Downloaded))
            {
                await PluginRuntime.SelectModelAsync(initial, CancellationToken.None);
                await PluginRuntime.RefreshCapabilitiesAsync();
            }
            if (!UsesRegistryProvider && Models.Enabled && !Models.Busy && Models.SavedModelId is { } selected && Models.ActiveModelId != selected
                && Models.Models.Any(item => item.Model.Id == selected && item.Downloaded))
                await Models.ActivateAsync(selected, CancellationToken.None, persistSelection: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.selection.apply-failed", ex); }
        finally
        {
            if (!_disposed && before != (_providerId, ActiveModelId))
            {
                AppDiagnostics.Write($"policy.selection.applied provider={_providerId} model={ActiveModelId}");
                SetStatus(IsReady ? ModelReadyStatus() : SelectionLockReason() ?? Loc.T("Choose and configure a transcription provider in Dictation."), DictationPhase.Idle);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Conflicts between the enforced pairs and what is installed, allowed or available; for the managed settings page.</summary>
    internal IEnumerable<string> SelectionPolicyIssues()
    {
        var policy = ProviderPolicy;
        if (policy.EnforcedTranscription is { } transcription)
        {
            var provider = DictationProviders.FirstOrDefault(item => item.Id == SessionProviderId(transcription.ProviderId));
            if (provider is null)
                yield return ManagedPolicyKeys.TranscriptionProviderId + ": " + Loc.T("The transcription provider {0} is not installed or enabled.", transcription.ProviderId);
            else
            {
                if (provider.Refusal is { } refused) yield return ManagedPolicyKeys.TranscriptionProviderId + ": " + refused;
                if (!provider.Models.Any(item => transcription.IsModel(item.Id)))
                    yield return ManagedPolicyKeys.TranscriptionModelId + ": " + Loc.T("The model {0} is not offered by {1}.", transcription.ModelId, provider.Name);
            }
        }
        if (policy.EnforcedLlm is { } llm)
        {
            var provider = LlmProviders.FirstOrDefault(item => string.Equals(item.SelectionId, llm.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                yield return ManagedPolicyKeys.LlmProviderId + ": " + Loc.T("The text model provider {0} is not installed or enabled.", llm.ProviderId);
            else
            {
                if (PluginRuntime.LlmRefusal(provider.SelectionId) is { } refused) yield return ManagedPolicyKeys.LlmProviderId + ": " + refused;
                if (!provider.Models.Any(item => llm.IsModel(item.Id)))
                    yield return ManagedPolicyKeys.LlmModelId + ": " + Loc.T("The model {0} is not offered by {1}.", llm.ModelId, provider.Name);
            }
        }
    }
}
