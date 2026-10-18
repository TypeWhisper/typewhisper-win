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

    // Policy names providers by their stable selection ID; the session calls the local engine "local" and the
    // built-in Groq role "groq". IDs compare case-insensitively, as the registry does.
    // Other IDs resolve to the installed provider's own spelling, so session lookups that compare exactly still find it.
    private string PolicyProviderId(string selectionId) =>
        string.Equals(selectionId, LocalTranscriptionPlugin.PluginId, StringComparison.OrdinalIgnoreCase) ? "local"
        : string.Equals(selectionId, CloudTranscriptionPlugin.PluginId, StringComparison.OrdinalIgnoreCase) ? "groq"
        : PluginRuntime.TranscriptionProviders.FirstOrDefault(provider => string.Equals(provider.SelectionId, selectionId, StringComparison.OrdinalIgnoreCase))?.SelectionId ?? selectionId;

    private bool IsProviderId(string policyId, string sessionId) =>
        string.Equals(PolicyProviderId(policyId), sessionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The model policy enforces for the active provider, or null; shown selected even before it is downloaded.</summary>
    internal string? EnforcedModelId => EnforcedTranscription is { } pair && IsProviderId(pair.ProviderId, _providerId) ? pair.ModelId : null;

    /// <summary>What the locked dictation row says: the required pair, or why it cannot be used yet.</summary>
    internal string? SelectionLockReason()
    {
        if (ProviderPolicy.TranscriptionSelectionBlocked)
            return Loc.T("Your organization's policy could not be read. This operation waits until it is readable.");
        if (EnforcedTranscription is not { } pair) return null;
        var provider = DictationProviders.FirstOrDefault(item => IsProviderId(pair.ProviderId, item.Id));
        var model = provider?.Models.FirstOrDefault(item => pair.IsModel(item.Id));
        return Loc.T("Managed by your organization") + ": " + (provider?.Name ?? pair.ProviderId) + " · " + (model?.Name ?? pair.ModelId)
            + (TranscriptionPolicyRefusal() is { } refused ? " · " + refused : "");
    }

    /// <summary>Why a user or API selection may not become the dictation selection, or null.</summary>
    internal string? SelectionRefusal(string providerId, string modelId)
    {
        if (ProviderPolicy.TranscriptionSelectionBlocked)
            return Loc.T("Your organization's policy could not be read. This operation waits until it is readable.");
        if (EnforcedTranscription is not { } pair) return null;
        return !IsProviderId(pair.ProviderId, providerId)
            ? Loc.T("Your organization requires the transcription provider {0}.", pair.ProviderId)
            : !pair.IsModel(modelId) ? Loc.T("Your organization requires the transcription model {0}.", pair.ModelId) : null;
    }

    // A dictation workflow's own model is ignored while policy fixes the selection: the enforced pair wins.
    private static string? WorkflowTranscriptionModel(AutomaticWorkflowSnapshot? workflow) =>
        TranscriptionSelectionLocked ? null : workflow?.TranscriptionModel;

    // The provider at startup: the enforced pair, then the user's saved choice, then a recommendation, then local.
    private string StartupProviderId() => EnforcedTranscription is { } enforced ? PolicyProviderId(enforced.ProviderId)
        : _selection.GetSetting<string>("Provider") is { } chosen ? SessionProviderId(chosen)
        : ProviderPolicy.RecommendedTranscription is { } recommended ? PolicyProviderId(recommended.ProviderId) : "local";

    // The local engine reads the pair when it enables itself, so the enforced model loads once instead of twice.
    internal static (string? Enforced, string? Recommended) LocalEnginePolicyModels()
    {
        var policy = ProviderPolicy;
        return (policy.EnforcedTranscription is { } pair && pair.IsProvider(LocalTranscriptionPlugin.PluginId) ? pair.ModelId : null,
            policy.RecommendedTranscription is { } recommended && recommended.IsProvider(LocalTranscriptionPlugin.PluginId) ? recommended.ModelId : null);
    }

    // Resolved to the installed provider's own spelling: workflow availability checks compare selection IDs exactly.
    private string PolicyLlmProviderId(string selectionId) =>
        LlmProviders.FirstOrDefault(provider => string.Equals(provider.SelectionId, selectionId, StringComparison.OrdinalIgnoreCase))?.SelectionId ?? selectionId;

    private WorkflowLlmSelection? EnforcedLlmSelection() =>
        ProviderPolicy.EnforcedLlm is { } pair ? new(PolicyLlmProviderId(pair.ProviderId), pair.ModelId) : null;

    private WorkflowLlmSelection? RecommendedLlmSelection() =>
        ProviderPolicy.RecommendedLlm is { } pair ? new(PolicyLlmProviderId(pair.ProviderId), pair.ModelId) : null;

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
                _providerId = PolicyProviderId(pair.ProviderId);
                if (!UsesRegistryProvider)
                {
                    // Activated under the catalog's own spelling of the required model.
                    if (Models.Enabled && !Models.Busy
                        && Models.Models.FirstOrDefault(item => pair.IsModel(item.Model.Id) && item.Downloaded) is { } required
                        && Models.ActiveModelId != required.Model.Id)
                        await Models.ActivateAsync(required.Model.Id, CancellationToken.None, persistSelection: false);
                }
                else if (ActiveRegistryProvider is { } provider && !pair.IsModel(provider.SelectedModelId ?? ""))
                {
                    var target = provider.ModelStates.FirstOrDefault(item => pair.IsModel(item.ModelId));
                    if (target is not null && (!target.SupportsDownload || target.Downloaded))
                    {
                        // A pair that moved from another registry provider restores that provider's own model first, so
                        // one retained selection is enough for every provider a policy replaced.
                        if (_selectionPolicy.Current.RetainedProviderId is { } other && other != provider.SelectionId)
                            await RestoreRetainedSelectionAsync();
                        if (_selectionPolicy.Current.RetainedProviderId is null && provider.SelectedModelId is not null)
                            _selectionPolicy.Save(new(provider.SelectionId, provider.SelectedModelId));
                        await PluginRuntime.SelectModelAsync(target, CancellationToken.None);
                        await PluginRuntime.RefreshCapabilitiesAsync();
                    }
                }
                return;
            }
            // Confirmed removal: the user's saved provider applies again and a retained registry model is selected once.
            await RestoreRetainedSelectionAsync();
            var chosen = _selection.GetSetting<string>("Provider");
            var saved = chosen is not null ? SessionProviderId(chosen)
                : policy.RecommendedTranscription is { } suggested ? PolicyProviderId(suggested.ProviderId) : "local";
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

    // Selects the retained registry model once and forgets it only after it applied: a provider that is disabled or a
    // model that is unavailable right now keeps the retention for a later pass. Runs with the gate held.
    private async Task RestoreRetainedSelectionAsync()
    {
        if (_selectionPolicy.Current is not { RetainedProviderId: { } retainedProvider, RetainedModelId: { } retainedModel }) return;
        var provider = PluginRuntime.TranscriptionProviders.FirstOrDefault(item => item.SelectionId == retainedProvider);
        if (provider is null) return;
        if (provider.SelectedModelId != retainedModel)
        {
            var target = provider.ModelStates.FirstOrDefault(item => item.ModelId == retainedModel);
            if (target is null || (target.SupportsDownload && !target.Downloaded)) return;
            await _livePreview.StopAsync();
            await PluginRuntime.SelectModelAsync(target, CancellationToken.None);
            await PluginRuntime.RefreshCapabilitiesAsync();
        }
        _selectionPolicy.Save(new());
    }

    /// <summary>Conflicts between the enforced pairs and what is installed, allowed or available; for the managed settings page.</summary>
    internal IEnumerable<string> SelectionPolicyIssues()
    {
        var policy = ProviderPolicy;
        if (policy.EnforcedTranscription is { } transcription)
        {
            var provider = DictationProviders.FirstOrDefault(item => IsProviderId(transcription.ProviderId, item.Id));
            if (provider is null)
                yield return ManagedPolicyKeys.TranscriptionProviderId + ": " + Loc.T("The transcription provider {0} is not installed or enabled.", transcription.ProviderId);
            else
            {
                if (provider.Refusal is { } refused) yield return ManagedPolicyKeys.TranscriptionProviderId + ": " + refused;
                else if (!provider.Enabled || !provider.Configured)
                    yield return ManagedPolicyKeys.TranscriptionProviderId + ": " + Loc.T("The transcription provider {0} is not installed or enabled.", transcription.ProviderId);
                if (provider.Models.FirstOrDefault(item => transcription.IsModel(item.Id)) is not { } required)
                    yield return ManagedPolicyKeys.TranscriptionModelId + ": " + Loc.T("The model {0} is not offered by {1}.", transcription.ModelId, provider.Name);
                else if (!required.Ready)
                    yield return ManagedPolicyKeys.TranscriptionModelId + ": " + Loc.T("The model {0} is not downloaded.", transcription.ModelId);
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
