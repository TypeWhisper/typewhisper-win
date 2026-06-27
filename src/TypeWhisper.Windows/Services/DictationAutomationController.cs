namespace TypeWhisper.Windows.Services;

/// <summary>
/// Describes the outcome of a local automation text insertion.
/// </summary>
/// <param name="InsertionResult">Text insertion result.</param>
/// <param name="TargetAppCorrectionLearningTrackingStarted">
/// Whether target-app correction learning started after insertion.
/// </param>
/// <param name="TargetAppCorrectionLearningSkipReason">
/// Optional diagnostic reason when correction learning did not start.
/// </param>
public sealed record DictationAutomationTextInsertionResult(
    InsertionResult InsertionResult,
    bool TargetAppCorrectionLearningTrackingStarted,
    string? TargetAppCorrectionLearningSkipReason = null);

/// <summary>
/// Provides local automation hooks for deterministic dictation and correction-learning tests.
/// </summary>
public interface IDictationAutomationController
{
    /// <summary>
    /// Inserts text into the active target app through the normal dictation insertion path.
    /// </summary>
    Task<DictationAutomationTextInsertionResult> InsertTextForAutomationAsync(
        string text,
        bool autoEnter,
        CancellationToken ct);

    /// <summary>
    /// Signals that the current target-app correction-learning observation should commit.
    /// </summary>
    void CommitTargetAppCorrectionLearningForAutomation();
}
