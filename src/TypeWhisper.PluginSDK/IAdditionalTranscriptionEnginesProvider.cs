namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional capability expansion for plugins that expose additional transcription engine roles.
/// </summary>
public interface IAdditionalTranscriptionEnginesProvider
{
    /// <summary>Additional transcription engine roles exposed by this plugin.</summary>
    IReadOnlyList<ITranscriptionEnginePlugin> AdditionalTranscriptionEngines { get; }
}
