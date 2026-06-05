namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional capability expansion for plugins that expose additional LLM provider roles.
/// </summary>
public interface IAdditionalLlmProvidersProvider
{
    /// <summary>Additional LLM provider roles exposed by this plugin.</summary>
    IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders { get; }
}
