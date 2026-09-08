namespace TypeWhisper.Presentation;

/// <summary>Explicit session read-back through the shared local speech controller.</summary>
public static class LastDictationReadback
{
    /// <summary>Stops active speech, otherwise reads the complete final snapshot using the selected voice and output.</summary>
    public static async Task<SpokenFeedbackResult> ToggleAsync(SpokenFeedbackController controller,
        LastCompletedDictation? snapshot, string? voiceId, string? outputDeviceId)
    {
        if (controller.IsBusy)
        {
            await controller.CancelAndDrainAsync();
            return new(SpokenFeedbackStatus.Canceled, "Read-back stopped.");
        }
        if (snapshot is null)
            return new(SpokenFeedbackStatus.Rejected, "No completed dictation in this session yet. Dictate once, then use this shortcut.");
        return await controller.SpeakAsync(new(snapshot.Text, snapshot.Language, voiceId, outputDeviceId));
    }
}
