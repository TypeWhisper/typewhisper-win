namespace TypeWhisper.ModuleKit;

/// <summary>
/// Tells who spoke when in an audio file (speaker diarization), with the diarization providers of the user's enabled
/// plugins. Speakers are anonymous voices; nothing identifies people.
/// </summary>
/// <remarks>
/// The host applies the managed policy before any audio reaches a provider: a provider that is not local receives audio
/// only while the organization allows cloud transcription, and model downloads follow the model download policy. A
/// refusal throws <see cref="ModulePolicyRefusedException"/>.
/// </remarks>
public interface IAudioDiarization
{
    /// <summary>The diarization providers of enabled plugins, in no particular order. Reading it runs no provider.</summary>
    IReadOnlyList<ModuleDiarizationProvider> Providers { get; }

    /// <summary>
    /// Calls <paramref name="handler"/> on the UI thread after <see cref="Providers"/> may have changed, for example when
    /// a plugin was enabled or a provider's models were installed, until the result is disposed.
    /// </summary>
    IDisposable ObserveChanges(Action handler);

    /// <summary>
    /// Downloads and verifies a provider's models, so that <see cref="ModuleDiarizationProvider.IsReady"/> becomes true.
    /// Does nothing when they are installed.
    /// </summary>
    /// <param name="providerId">A <see cref="ModuleDiarizationProvider.Id"/>.</param>
    /// <param name="progress">Receives the completed fraction between 0 and 1, on any thread.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="ModulePolicyRefusedException">The managed policy turns model downloads off.</exception>
    /// <exception cref="InvalidOperationException">The provider is no longer available, or the download failed.</exception>
    Task PrepareAsync(string providerId, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the speaker turns of an audio or video file the host can decode, of up to 60 minutes. Turns are sorted by
    /// start; speakers are labelled <c>S1</c>, <c>S2</c>, … in the order they first speak.
    /// </summary>
    /// <param name="providerId">A <see cref="ModuleDiarizationProvider.Id"/> whose provider is ready.</param>
    /// <param name="audioFilePath">The full path of the file. The host only reads it.</param>
    /// <param name="options">Optional hints, or null to detect the number of speakers.</param>
    /// <param name="progress">Receives the completed fraction between 0 and 1, on any thread.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The turns; empty when the audio contains no speech. Turns of different speakers may overlap.</returns>
    /// <exception cref="ModulePolicyRefusedException">The managed policy refuses audio for this provider.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The speaker count is outside the provider's range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The provider is not available or not ready, the file cannot be decoded, or the provider failed. The message can
    /// be shown.
    /// </exception>
    Task<IReadOnlyList<ModuleSpeakerTurn>> DiarizeAsync(string providerId, string audioFilePath,
        ModuleDiarizationOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>A diarization provider of an enabled plugin.</summary>
/// <param name="Id">The provider's stable identifier.</param>
/// <param name="DisplayName">The provider's name for the UI.</param>
/// <param name="IsLocal">Whether the audio stays on this device. Otherwise the provider sends it to a service.</param>
/// <param name="IsReady">Whether <see cref="IAudioDiarization.DiarizeAsync"/> can run without a download or more setup.</param>
public sealed record ModuleDiarizationProvider(string Id, string DisplayName, bool IsLocal, bool IsReady)
{
    /// <summary>Whether the provider still needs its models; <see cref="IAudioDiarization.PrepareAsync"/> installs them.</summary>
    public bool NeedsModels { get; init; }

    /// <summary>The download size of the provider's models for the UI, such as <c>"~380 MB"</c>, or null.</summary>
    public string? ModelSize { get; init; }

    /// <summary>The smallest fixed speaker count the provider accepts, or null when it only detects the count.</summary>
    public int? MinimumSpeakerCount { get; init; }

    /// <summary>The largest fixed speaker count the provider accepts, or null when it only detects the count.</summary>
    public int? MaximumSpeakerCount { get; init; }
}

/// <summary>Optional hints for a diarization.</summary>
/// <param name="SpeakerCount">A fixed number of speakers within the provider's range, or null to detect it.</param>
/// <param name="Language">The spoken language as an ISO code, or null when unknown. Providers may ignore it.</param>
public sealed record ModuleDiarizationOptions(int? SpeakerCount = null, string? Language = null);

/// <summary>A stretch of audio that one speaker speaks in.</summary>
/// <param name="Speaker">The anonymous speaker label, <c>S1</c>, <c>S2</c>, … in the order they first speak.</param>
/// <param name="StartSeconds">Where the turn starts, in seconds from the start of the file.</param>
/// <param name="EndSeconds">Where the turn ends, after <paramref name="StartSeconds"/>.</param>
/// <param name="Confidence">How sure the provider is about the turn, from 0 to 1, or null when it does not say.</param>
public sealed record ModuleSpeakerTurn(string Speaker, double StartSeconds, double EndSeconds, double? Confidence = null);

/// <summary>The organization's managed policy refuses an operation. The message is safe to show.</summary>
public sealed class ModulePolicyRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the policy refuses the operation.</param>
    /// <param name="innerException">The host's refusal, if any.</param>
    public ModulePolicyRefusedException(string message, Exception? innerException = null) : base(message, innerException) { }
}
