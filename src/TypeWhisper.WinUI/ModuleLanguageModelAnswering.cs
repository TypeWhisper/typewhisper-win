using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;
using TypeWhisper.PluginSDK;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

/// <summary>An enabled text model provider as the host lists it.</summary>
internal sealed record ModuleLlmProviderState(string SelectionId, string Name, string PluginId, bool IsLocal, bool Ready, IReadOnlyList<string> ModelIds);

// The host's text model plumbing ILanguageModelAnswering needs, so the mapping and policy translation run without a
// dictation session in tests.
internal sealed record ModuleLanguageModelRuntime(
    // The pair the managed policy enforces for every request, or null.
    Func<WorkflowLlmSelection?> Enforced,
    // Why the policy blocks every text model request until it is readable and valid, or null.
    Func<string?> Blocked,
    // The user's default workflow LLM, else the administrator's recommendation, or null.
    Func<WorkflowLlmSelection?> Preferred,
    Func<IReadOnlyList<ModuleLlmProviderState>> Providers,
    // Why the policy refuses text for a provider and model, or null.
    Func<string, string?, string?> Refusal,
    // Sends the system prompt and text to a provider and model exactly as given, under the policy.
    Func<string, string, string, string, CancellationToken, Task<string>> Process,
    // Loads a local provider's remembered model without sending text.
    Func<string, string, CancellationToken, Task> Prewarm,
    Action ShowSettings);

// ILanguageModelAnswering over the workflow LLM plumbing. The prompt reaches the provider as it is: no dictation
// processing, no memories. Every request is judged by the managed policy before it starts and again while it runs.
internal sealed class ModuleLanguageModelAnswering : ILanguageModelAnswering
{
    private readonly ModuleLanguageModelRuntime _runtime;
    private readonly Action<string, Exception> _report;

    internal ModuleLanguageModelAnswering(ModuleLanguageModelRuntime runtime, Action<string, Exception> report)
    {
        _runtime = runtime;
        _report = report;
    }

    public IReadOnlyList<ModuleLanguageModelProvider> Providers()
    {
        if (_runtime.Blocked() is { } blocked) throw Refused(blocked);
        if (_runtime.Enforced() is { } enforced)
        {
            var state = Find(enforced.Provider)
                ?? throw Refused($"Your organization requires the text model provider {enforced.Provider}, which is not available.");
            if (_runtime.Refusal(state.SelectionId, enforced.Model) is { } refused) throw Refused(refused);
            return [ToProvider(enforced, state)];
        }
        return _runtime.Preferred() is { } preferred ? [ToProvider(preferred, Find(preferred.Provider))] : [];
    }

    public async Task<string> AnswerAsync(string instructions, string prompt, ModuleLanguageModelProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        var (selectionId, model) = Check(provider);
        string reply;
        try { reply = await _runtime.Process(selectionId, instructions, prompt, model, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw ModuleError(ex, cancellationToken); }
        reply = reply?.Trim() ?? "";
        return reply.Length > 0 ? reply : throw new ModuleLanguageModelException(ModuleLanguageModelErrorKind.EmptyReply);
    }

    // No provider of the host counts tokens.
    public Task<ModuleTokenCount?> InputTokensAsync(string instructions, string prompt, ModuleLanguageModelProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(provider);
        return Task.FromResult<ModuleTokenCount?>(null);
    }

    public void Prewarm()
    {
        try
        {
            if (Providers().FirstOrDefault() is not { Kind: ModuleLanguageModelKind.Local, ModelId: { Length: > 0 } model } first
                || Find(first.Id) is not { } state || _runtime.Refusal(state.SelectionId, model) is not null) return;
            _ = PrewarmAsync(state.SelectionId, model);
        }
        catch (ModuleLanguageModelException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _report("modules.language-model.prewarm-failed", ex); }
    }

    public void ShowSettings() => _runtime.ShowSettings();

    private async Task PrewarmAsync(string selectionId, string model)
    {
        try { await _runtime.Prewarm(selectionId, model, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _report("modules.language-model.prewarm-failed", ex); }
    }

    // The provider and model a request uses, after the checks a workflow makes before it runs its LLM.
    private (string SelectionId, string Model) Check(ModuleLanguageModelProvider provider)
    {
        if (_runtime.Blocked() is { } blocked) throw Refused(blocked);
        // The organization may have enforced another pair since the module read the providers.
        if (_runtime.Enforced() is { } enforced && (!string.Equals(enforced.Provider, provider.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(enforced.Model, provider.ModelId, StringComparison.Ordinal)))
            throw Refused("Your organization does not allow this language model provider.");
        var state = Find(provider.Id);
        if (_runtime.Refusal(state?.SelectionId ?? provider.Id, provider.ModelId) is { } refused) throw Refused(refused);
        if (state is null) throw NotSetUp($"{provider.Name} is not available. Turn it on in Integrations.");
        if (!state.Ready) throw NotSetUp($"{state.Name} is not ready. Check its settings in Integrations.");
        if (provider.ModelId is not { Length: > 0 } model || !state.ModelIds.Contains(model, StringComparer.Ordinal))
            throw NotSetUp($"{state.Name} does not offer the chosen model. Choose another one for the default workflow LLM.");
        return (state.SelectionId, model);
    }

    private ModuleLlmProviderState? Find(string id) =>
        _runtime.Providers().FirstOrDefault(state => string.Equals(state.SelectionId, id, StringComparison.OrdinalIgnoreCase));

    private static ModuleLanguageModelProvider ToProvider(WorkflowLlmSelection selection, ModuleLlmProviderState? state) =>
        // A provider that is not installed counts as one that sends text away, so a module asks before using it.
        new(state?.SelectionId ?? selection.Provider, state?.Name ?? selection.Provider, state?.IsLocal == true ? ModuleLanguageModelKind.Local : ModuleLanguageModelKind.Cloud)
        {
            PluginId = state?.PluginId,
            ModelId = string.IsNullOrEmpty(selection.Model) ? null : selection.Model
        };

    /// <summary>The error a module sees: its own cancellation stays a cancellation, the rest becomes <see cref="ModuleLanguageModelException"/>.</summary>
    internal static Exception ModuleError(Exception error, CancellationToken cancellationToken) => error switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => error,
        ModuleLanguageModelException => error,
        ManagedPolicyDeniedException refused => Refused(refused.Message, refused),
        PluginRequestException { FailureKind: PluginRequestFailureKind.RequestTooLarge } large =>
            new ModuleLanguageModelException(ModuleLanguageModelErrorKind.ContextExceeded, large.Message, large),
        PluginRequestException { FailureKind: PluginRequestFailureKind.EmptyResponse } empty =>
            new ModuleLanguageModelException(ModuleLanguageModelErrorKind.EmptyReply, null, empty),
        PluginRequestException { FailureKind: PluginRequestFailureKind.Authentication or PluginRequestFailureKind.Permission
            or PluginRequestFailureKind.Configuration } setup => NotSetUp(setup.Message, setup),
        OperationCanceledException canceled =>
            new ModuleLanguageModelException(ModuleLanguageModelErrorKind.Failed, "The language model provider stopped the request.", canceled),
        _ => new ModuleLanguageModelException(ModuleLanguageModelErrorKind.Failed,
            string.IsNullOrWhiteSpace(error.Message) ? "Unknown provider failure." : error.Message.Trim(), error)
    };

    private static ModuleLanguageModelException Refused(string message, Exception? inner = null) =>
        new(ModuleLanguageModelErrorKind.Refused, message, inner);

    private static ModuleLanguageModelException NotSetUp(string message, Exception? inner = null) =>
        new(ModuleLanguageModelErrorKind.NotSetUp, message, inner);
}
