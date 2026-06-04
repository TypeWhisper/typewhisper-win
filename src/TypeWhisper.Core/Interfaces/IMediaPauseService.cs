namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the media pause service contract.
/// </summary>
public interface IMediaPauseService
{
    /// <summary>
    /// Pauses media.
    /// </summary>
    void PauseMedia();
    /// <summary>
    /// Resumes media.
    /// </summary>
    void ResumeMedia();
}
