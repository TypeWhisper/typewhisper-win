using System.Runtime.ExceptionServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;
using TypeWhisper.PluginSDK;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// What a spoken input needs from the dictation session, so its states, endings and errors run with a fake microphone
// and clock in tests.
internal interface ISpeechInputRuntime
{
    // Monotonic time, readable from any thread.
    TimeSpan Now { get; }

    // Whether the app is shutting down.
    bool Closing { get; }

    // Why the policy refuses the user's transcription engine now, or null.
    string? Refusal();

    // Reserves the microphone and the dictation engine and opens the microphone, which reports its loudness (RMS) to
    // level on any thread. Throws ModuleSpeechInputException when it cannot; then nothing stays reserved.
    Task<ISpeechInputCapture> OpenAsync(Action<float> level, CancellationToken cancellationToken);

    // Calls tick on the UI thread about every interval until the result is disposed.
    IDisposable Every(TimeSpan interval, Action tick);
}

// An open microphone with the engine reserved for it. Disposing discards a running recording and releases both.
internal interface ISpeechInputCapture : IAsyncDisposable
{
    // Canceled with the caller's token, and when the host stops the input: at shutdown or for the policy.
    CancellationToken Cancellation { get; }

    // False once the microphone was lost.
    bool Recording { get; }

    // The current loudness (RMS).
    float Level { get; }

    // The user's choice to transcribe short, quiet recordings.
    bool RecognizeQuietClips { get; }

    // Ends the recording and returns the 16 kHz mono samples with the loudest level before gain.
    Task<SpeechInputAudio> StopAsync();

    // Transcribes the samples with the engine reserved at the start and applies the dictionary. Empty when no speech
    // was recognized.
    Task<SpeechInputTranscript> TranscribeAsync(float[] samples, CancellationToken cancellationToken);
}

internal sealed record SpeechInputAudio(float[]? Samples, float PeakRms);

internal sealed record SpeechInputTranscript(string Text, string? Language);

// ISpeechInput over the dictation session: one input at a time, which ends by Stop, by silence after speech or at the
// maximum duration, and is discarded when canceled.
internal sealed class ModuleSpeechInput : ISpeechInput
{
    internal const int SampleRate = 16000;
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    // The level meter shows 60 dB below full scale as silent.
    private const double MeterRangeDb = 60;

    private readonly ISpeechInputRuntime _runtime;
    private readonly Action<string> _write;
    private Listening? _current;

    internal ModuleSpeechInput(ISpeechInputRuntime runtime, Action<string> write)
    {
        _runtime = runtime;
        _write = write;
    }

    public async Task<ModuleSpeechInputResult> ListenAsync(ModuleSpeechInputOptions? options = null, IProgress<ModuleSpeechInputState>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (silenceTimeout, maximumDuration) = Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (_current is not null)
        {
            _write("modules.speech-input.failed kind=busy");
            throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Busy, BusyMessage);
        }
        var listening = _current = new Listening();
        _write("modules.speech-input.start");
        try
        {
            var result = await RunAsync(listening, silenceTimeout, maximumDuration, progress, cancellationToken);
            _write("modules.speech-input.completed ending=" + result.Ending.ToString().ToLowerInvariant());
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var error = ModuleError(ex, cancellationToken, _runtime.Refusal, _runtime.Closing);
            _write(error is ModuleSpeechInputException failed
                ? "modules.speech-input.failed kind=" + failed.Kind.ToString().ToLowerInvariant() : "modules.speech-input.canceled");
            if (ReferenceEquals(error, ex)) ExceptionDispatchInfo.Throw(ex);
            throw error;
        }
        finally { _current = null; }
    }

    public void Stop() => _current?.End(ModuleSpeechInputEnding.Stopped);

    private async Task<ModuleSpeechInputResult> RunAsync(Listening listening, TimeSpan? silenceTimeout, TimeSpan maximumDuration,
        IProgress<ModuleSpeechInputState>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(ModuleSpeechInputPhase.Starting));
        // Without a timeout, silence is still tracked to report whether speech was heard.
        var silence = new SilenceAutoStop(silenceTimeout ?? TimeSpan.MaxValue);
        // Silence is judged on the runtime's clock; the duration counts from when the microphone records, so its setup
        // does not shorten the input.
        await using var capture = await _runtime.OpenAsync(level => silence.Observe(_runtime.Now, level), cancellationToken);
        var origin = _runtime.Now;
        var token = capture.Cancellation;
        ModuleSpeechInputEnding ending;
        using (token.Register(listening.Cancel))
        {
            token.ThrowIfCancellationRequested();
            void Tick()
            {
                if (listening.Ended) return;
                if (!capture.Recording)
                {
                    listening.Fail(new ModuleSpeechInputException(ModuleSpeechInputErrorKind.MicrophoneUnavailable,
                        Loc.T("Microphone disconnected · recording stopped")));
                    return;
                }
                // The policy may refuse the engine while the input records; nothing more is recorded for it.
                if (_runtime.Refusal() is { } refused)
                {
                    listening.Fail(new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Refused, refused));
                    return;
                }
                var now = _runtime.Now;
                var elapsed = now - origin;
                if (elapsed >= maximumDuration) listening.End(ModuleSpeechInputEnding.MaximumDuration);
                else if (silence.HasActivity && silence.ShouldStop(now, false)) listening.End(ModuleSpeechInputEnding.Silence);
                else progress?.Report(new(ModuleSpeechInputPhase.Listening)
                    { Level = Loudness(capture.Level), Elapsed = elapsed, HeardSpeech = silence.HasActivity });
            }
            Tick();
            using (_runtime.Every(TickInterval, Tick)) ending = await listening.Task;
        }
        progress?.Report(new(ModuleSpeechInputPhase.Transcribing));
        var audio = await capture.StopAsync();
        token.ThrowIfCancellationRequested();
        var samples = audio.Samples ?? [];
        var duration = samples.Length / (double)SampleRate;
        if (ShortClipCapturePolicy.Classify(duration, audio.PeakRms, false, capture.RecognizeQuietClips) != ShortClipCaptureDecision.Transcribe)
            throw NoSpeech();
        var transcript = await capture.TranscribeAsync(samples, token);
        token.ThrowIfCancellationRequested();
        var text = transcript.Text?.Trim();
        if (string.IsNullOrEmpty(text)) throw NoSpeech();
        progress?.Report(new(ModuleSpeechInputPhase.Done));
        return new(text, transcript.Language, duration) { Ending = ending };
    }

    internal static string BusyMessage => Loc.T("Finish the current dictation, recording or transcription first.");

    private static ModuleSpeechInputException NoSpeech() => new(ModuleSpeechInputErrorKind.NoSpeech, Loc.T("No speech was recognized."));

    /// <summary>
    /// The text a spoken input returns: the dictionary and the text settings as for a dictation, but no snippets,
    /// workflows or text processors, and no formatting for a target app.
    /// </summary>
    internal static async Task<string> ProcessTextAsync(string text, DictationDictionarySnapshot? dictionary, DictationTextPreferences preferences,
        string configuredLanguage, string? detectedLanguage, bool boostVocabulary, string engineId, string? modelId, CancellationToken cancellationToken) =>
        (await new DictationLexiconSnapshot(dictionary, null).ProcessAsync(text, preferences, configuredLanguage, detectedLanguage, boostVocabulary,
            _ => Task.FromResult(""), cancellationToken, TranscriptionTask.Transcribe, null, engineId, modelId)).Text;

    /// <summary>The options with their defaults, or <see cref="ArgumentOutOfRangeException"/> for one outside its range.</summary>
    internal static (TimeSpan? SilenceTimeout, TimeSpan MaximumDuration) Validate(ModuleSpeechInputOptions? options)
    {
        options ??= new();
        if (options.SilenceTimeout is { } silence
            && (silence < ModuleSpeechInputOptions.MinimumSilenceTimeout || silence > ModuleSpeechInputOptions.LongestSilenceTimeout))
            throw new ArgumentOutOfRangeException(nameof(options), silence,
                $"The silence timeout must be null or between {ModuleSpeechInputOptions.MinimumSilenceTimeout.TotalSeconds} and {ModuleSpeechInputOptions.LongestSilenceTimeout.TotalSeconds} seconds.");
        if (options.MaximumDuration < ModuleSpeechInputOptions.MinimumDuration || options.MaximumDuration > ModuleSpeechInputOptions.LongestDuration)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaximumDuration,
                $"The maximum duration must be between {ModuleSpeechInputOptions.MinimumDuration.TotalSeconds} and {ModuleSpeechInputOptions.LongestDuration.TotalSeconds} seconds.");
        return (options.SilenceTimeout, options.MaximumDuration);
    }

    /// <summary>A loudness (RMS) as a level from 0 at 60 dB below full scale to 1 at full scale.</summary>
    internal static double Loudness(float rms) =>
        !float.IsFinite(rms) || rms <= 0 ? 0 : Math.Clamp((20 * Math.Log10(rms) + MeterRangeDb) / MeterRangeDb, 0, 1);

    /// <summary>
    /// The error a module sees: its own cancellation stays a cancellation, everything else becomes a
    /// <see cref="ModuleSpeechInputException"/>. A cancellation by the host is a refusal while the policy refuses the
    /// engine, and a failure otherwise.
    /// </summary>
    internal static Exception ModuleError(Exception error, CancellationToken cancellationToken, Func<string?> refusal, bool closing)
    {
        if (cancellationToken.IsCancellationRequested)
            return error as OperationCanceledException ?? new OperationCanceledException(error.Message, error, cancellationToken);
        return error switch
        {
            ModuleSpeechInputException => error,
            ManagedPolicyDeniedException denied => Refused(denied.Message, denied),
            { InnerException: ManagedPolicyDeniedException denied } => Refused(denied.Message, error),
            OperationCanceledException when refusal() is { } refused => Refused(refused, error),
            OperationCanceledException or ObjectDisposedException when closing => new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Failed, Loc.T("The app is closing."), error),
            PluginRequestException { FailureKind: PluginRequestFailureKind.Authentication or PluginRequestFailureKind.Permission
                or PluginRequestFailureKind.Configuration } setup => new ModuleSpeechInputException(ModuleSpeechInputErrorKind.NotSetUp, setup.Message, setup),
            _ => new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Failed,
                Loc.T("Transcription failed: {0}", string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message.Trim()), error)
        };
    }

    private static ModuleSpeechInputException Refused(string message, Exception inner) =>
        new(ModuleSpeechInputErrorKind.Refused, message, inner);

    // The running input: how its recording ended, once it has.
    private sealed class Listening
    {
        private readonly TaskCompletionSource<ModuleSpeechInputEnding> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<ModuleSpeechInputEnding> Task => _ended.Task;
        internal bool Ended => _ended.Task.IsCompleted;
        internal void End(ModuleSpeechInputEnding ending) => _ended.TrySetResult(ending);
        internal void Fail(Exception error) => _ended.TrySetException(error);
        internal void Cancel() => _ended.TrySetCanceled();
    }
}
