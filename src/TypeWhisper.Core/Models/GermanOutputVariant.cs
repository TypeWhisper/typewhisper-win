namespace TypeWhisper.Core.Models;

/// <summary>
/// Lists the supported regional variants for written German output.
/// </summary>
public enum GermanOutputVariant
{
    /// <summary>
    /// Preserves the spelling returned by transcription and post-processing.
    /// </summary>
    AsTranscribed,

    /// <summary>
    /// Selects the written German variant used in Germany.
    /// </summary>
    Germany,

    /// <summary>
    /// Selects the written German variant used in Austria.
    /// </summary>
    Austria,

    /// <summary>
    /// Selects Swiss Standard German spelling.
    /// </summary>
    Switzerland
}
