namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// What the host transcribes, so a provider can treat a dictation differently from audio that needs the words as
/// spoken. Read it through <see cref="TranscriptionRequestContext.Purpose"/>. Plugin API 1.3.1.
/// </summary>
public enum TranscriptionPurpose
{
    /// <summary>
    /// Audio whose transcript keeps the words as spoken: recordings, file transcriptions, watch folders, the HTTP API's
    /// transcription routes, dictionary training and anything else that is not a dictation. Speaker labels apply when
    /// the provider is set up for them. Hosts before plugin API 1.3.1 report this for every request.
    /// </summary>
    Verbatim,
    /// <summary>
    /// A dictation: the host inserts or shows the text, also for a dictation started through a workflow shortcut or the
    /// HTTP API, and for the retry of a failed dictation. A provider may return the text cleaned up, for example without
    /// filler words, and should leave speaker labels out.
    /// </summary>
    Dictation
}

/// <summary>
/// The purpose of the transcription calls the host makes in the current asynchronous flow. Plugin API 1.3.1.
/// </summary>
/// <remarks>
/// The value flows with the call like an <see cref="AsyncLocal{T}"/>: a plugin reads <see cref="Purpose"/> in the
/// transcription or streaming call the host made, before it starts work that runs on its own, and keeps the value for
/// that request. The host sets it for <see cref="ITranscriptionEnginePlugin"/> transcription calls, the start of a live
/// stream and, for engines it runs in a worker process, in the worker. A plugin that reads it needs a
/// <c>minHostVersion</c> of at least 1.3.1, since older hosts lack this type.
/// </remarks>
public static class TranscriptionRequestContext
{
    private static readonly AsyncLocal<TranscriptionPurpose> CurrentPurpose = new();

    /// <summary>What the host transcribes in the current call; <see cref="TranscriptionPurpose.Verbatim"/> unless the host set otherwise.</summary>
    public static TranscriptionPurpose Purpose => CurrentPurpose.Value;

    /// <summary>
    /// Sets <see cref="Purpose"/> for the calls made in this flow until the returned scope is disposed, which restores
    /// the previous value. For hosts; plugins only read <see cref="Purpose"/>.
    /// </summary>
    public static IDisposable Begin(TranscriptionPurpose purpose)
    {
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        var scope = new Scope(CurrentPurpose.Value);
        CurrentPurpose.Value = purpose;
        return scope;
    }

    private sealed class Scope(TranscriptionPurpose previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) CurrentPurpose.Value = previous;
        }
    }
}
