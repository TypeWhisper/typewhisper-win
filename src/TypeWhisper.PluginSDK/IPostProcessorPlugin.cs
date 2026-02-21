using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Plugin that processes transcribed text after transcription (e.g. formatting, filtering).
/// Plugins are executed in ascending <see cref="Priority"/> order.
/// </summary>
public interface IPostProcessorPlugin : ITypeWhisperPlugin
{
    /// <summary>Display name for this processor.</summary>
    string ProcessorName { get; }

    /// <summary>Execution priority. Lower values run first.</summary>
    int Priority { get; }

    /// <summary>Processes the transcribed text and returns the modified version.</summary>
    Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct);
}
