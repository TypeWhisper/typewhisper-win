namespace TypeWhisper.ModuleKit;

/// <summary>Sends prompts to the host's text model (LLM) providers.</summary>
/// <remarks>
/// The host applies the managed policy to every request, as for workflows: an enforced provider and model, the allowed
/// providers and plugins, and whether text may go to cloud providers. A module tries the providers in the order of
/// <see cref="Providers"/> and decides itself what to do when one fails. Asking the user before text goes to a cloud
/// provider is the module's job.
/// </remarks>
public interface ILanguageModelAnswering
{
    /// <summary>
    /// The providers a request can try, in order: the provider and model an organization enforces, or the user's
    /// default workflow LLM. Empty when the user has not chosen one. Reading it runs no provider.
    /// </summary>
    /// <exception cref="ModuleLanguageModelException">
    /// With <see cref="ModuleLanguageModelErrorKind.Refused"/> when the enforced provider cannot be used.
    /// </exception>
    IReadOnlyList<ModuleLanguageModelProvider> Providers();

    /// <summary>
    /// The provider's reply to a prompt, trimmed. The prompt is answered as it is: the host adds no memories and does
    /// not treat it as dictated text.
    /// </summary>
    /// <param name="instructions">The system prompt.</param>
    /// <param name="prompt">The user prompt.</param>
    /// <param name="provider">One of <see cref="Providers"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ModuleLanguageModelException">The provider gave no answer; <see cref="ModuleLanguageModelException.Kind"/> says why.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<string> AnswerAsync(string instructions, string prompt, ModuleLanguageModelProvider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tokens a request takes and the tokens the model's context holds, or null where the provider cannot count
    /// them.
    /// </summary>
    Task<ModuleTokenCount?> InputTokensAsync(string instructions, string prompt, ModuleLanguageModelProvider provider,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts loading the first provider's model when it runs on this PC, so a request shortly afterwards does not
    /// wait for it. Sends no text and never fails.
    /// </summary>
    void Prewarm();

    /// <summary>Opens the settings where the user chooses the default workflow LLM.</summary>
    void ShowSettings();
}

/// <summary>Where a provider processes text.</summary>
public enum ModuleLanguageModelKind
{
    /// <summary>The model runs on this PC.</summary>
    Local,
    /// <summary>The text leaves this PC.</summary>
    Cloud
}

/// <summary>One provider with the model a request uses.</summary>
/// <param name="Id">The provider's identifier.</param>
/// <param name="Name">The provider's name for the UI.</param>
/// <param name="Kind">Where the provider processes text.</param>
public sealed record ModuleLanguageModelProvider(string Id, string Name, ModuleLanguageModelKind Kind)
{
    /// <summary>The plugin that offers the provider, or null when unknown.</summary>
    public string? PluginId { get; init; }

    /// <summary>The model a request uses, or null when none is chosen.</summary>
    public string? ModelId { get; init; }

    /// <summary>The reasoning effort a request uses, or null for the provider's default.</summary>
    public string? EffortId { get; init; }
}

/// <summary>The tokens a request takes and the tokens a model's context holds.</summary>
/// <param name="Count">The tokens of the request.</param>
/// <param name="ContextSize">The tokens the model's context holds.</param>
public sealed record ModuleTokenCount(int Count, int ContextSize);

/// <summary>Why a provider gave no answer.</summary>
public enum ModuleLanguageModelErrorKind
{
    /// <summary>The prompt is longer than the model's context. A shorter one may work.</summary>
    ContextExceeded,
    /// <summary>The provider is missing, not ready, or lacks its model or key.</summary>
    NotSetUp,
    /// <summary>The managed policy does not allow the request. The message says why.</summary>
    Refused,
    /// <summary>The reply was empty.</summary>
    EmptyReply,
    /// <summary>The provider failed.</summary>
    Failed
}

/// <summary>A provider gave no answer. The message is safe to show.</summary>
public sealed class ModuleLanguageModelException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">Why the provider gave no answer.</param>
    /// <param name="message">A message that can be shown, or null for a general one.</param>
    /// <param name="innerException">The host's error, if any.</param>
    public ModuleLanguageModelException(ModuleLanguageModelErrorKind kind, string? message = null, Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(message) ? DefaultMessage(kind) : message, innerException) => Kind = kind;

    /// <summary>Why the provider gave no answer.</summary>
    public ModuleLanguageModelErrorKind Kind { get; }

    private static string DefaultMessage(ModuleLanguageModelErrorKind kind) => kind switch
    {
        ModuleLanguageModelErrorKind.ContextExceeded => "The input is too long for the model.",
        ModuleLanguageModelErrorKind.NotSetUp => "The language model provider is not set up.",
        ModuleLanguageModelErrorKind.Refused => "Your organization does not allow this request.",
        ModuleLanguageModelErrorKind.EmptyReply => "The model returned an empty reply.",
        _ => "The language model provider failed."
    };
}
