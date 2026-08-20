namespace TypeWhisper.Core.Models;

/// <summary>
/// Lists the supported regional variants for written English output.
/// </summary>
public enum EnglishOutputVariant
{
    /// <summary>
    /// Preserves the spelling returned by transcription and post-processing.
    /// </summary>
    AsTranscribed,

    /// <summary>
    /// Selects American English spelling.
    /// </summary>
    UnitedStates,

    /// <summary>
    /// Selects British English spelling.
    /// </summary>
    UnitedKingdom
}
