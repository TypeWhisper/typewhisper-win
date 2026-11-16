using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;
using TypeWhisper.PluginSDK;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ModuleSpeechInputTests
{
    private static readonly TimeSpan Step = ModuleSpeechInput.TickInterval;
    private const float Speech = 0.05f;

    [Fact]
    public async Task StopEndsTheRecordingAndEveryStateIsReportedInOrder()
    {
        var runtime = new FakeRuntime();
        var written = new List<string>();
        var states = new StateLog();
        var input = new ModuleSpeechInput(runtime, written.Add);

        var listen = input.ListenAsync(null, states);
        runtime.Run(TimeSpan.FromSeconds(0.5), Speech);
        input.Stop();
        var result = await listen;

        Assert.Equal("hello world", result.Text);
        Assert.Equal("en", result.Language);
        Assert.Equal(1.0, result.DurationSeconds);
        Assert.Equal(ModuleSpeechInputEnding.Stopped, result.Ending);
        var phases = states.Items.Select(state => state.Phase).ToArray();
        Assert.Equal(ModuleSpeechInputPhase.Starting, phases[0]);
        Assert.All(phases[1..^2], phase => Assert.Equal(ModuleSpeechInputPhase.Listening, phase));
        Assert.Equal([ModuleSpeechInputPhase.Transcribing, ModuleSpeechInputPhase.Done], phases[^2..]);
        Assert.False(states.Items[1].HeardSpeech);
        Assert.True(states.Items[^3].HeardSpeech);
        Assert.InRange(states.Items[^3].Level, 0.01, 1);
        Assert.True(runtime.Capture!.Stopped);
        Assert.True(runtime.Capture.Disposed);
        Assert.Equal(["modules.speech-input.start", "modules.speech-input.completed ending=stopped"], written);
    }

    [Fact]
    public async Task EveryStateIsReportedThroughTheUiThreadDispatch()
    {
        var runtime = new FakeRuntime();
        var states = new StateLog();
        var input = new ModuleSpeechInput(runtime, _ => { });

        var listen = input.ListenAsync(null, states);
        runtime.Run(TimeSpan.FromSeconds(0.3), Speech);
        input.Stop();
        await listen;

        // Starting, the first Listening, three ticks, Transcribing and Done.
        Assert.Equal(7, states.Items.Count);
        Assert.Equal(0, states.Undispatched);
    }

    [Fact]
    public async Task SilenceEndsTheRecordingOnlyAfterSpeechWasHeard()
    {
        var runtime = new FakeRuntime();
        var states = new StateLog();
        var listen = new ModuleSpeechInput(runtime, _ => { }).ListenAsync(null, states);

        // Five silent seconds before anyone speaks do not end it, more than two seconds after speech do.
        runtime.Run(TimeSpan.FromSeconds(5), 0);
        runtime.Run(TimeSpan.FromSeconds(1), Speech);
        runtime.Run(TimeSpan.FromSeconds(4), 0);
        var result = await listen;

        Assert.Equal(ModuleSpeechInputEnding.Silence, result.Ending);
        var listening = states.Items.Where(state => state.Phase == ModuleSpeechInputPhase.Listening).ToArray();
        Assert.All(listening.Where(state => state.Elapsed <= TimeSpan.FromSeconds(5)), state => Assert.False(state.HeardSpeech));
        Assert.Equal(TimeSpan.FromSeconds(7.9), listening[^1].Elapsed);
    }

    [Fact]
    public async Task WithoutASilenceTimeoutOnlyStopOrTheMaximumEndsTheRecording()
    {
        var runtime = new FakeRuntime();
        var states = new StateLog();
        var input = new ModuleSpeechInput(runtime, _ => { });
        var listen = input.ListenAsync(new() { SilenceTimeout = null }, states);

        runtime.Run(TimeSpan.FromSeconds(1), Speech);
        runtime.Run(TimeSpan.FromSeconds(20), 0);
        input.Stop();
        var result = await listen;

        Assert.Equal(ModuleSpeechInputEnding.Stopped, result.Ending);
        Assert.Equal(TimeSpan.FromSeconds(21), states.Items.Last(state => state.Phase == ModuleSpeechInputPhase.Listening).Elapsed);
    }

    [Fact]
    public async Task TheMaximumDurationEndsARecordingThatNeverFallsSilent()
    {
        var runtime = new FakeRuntime();
        var states = new StateLog();
        var listen = new ModuleSpeechInput(runtime, _ => { }).ListenAsync(new() { MaximumDuration = TimeSpan.FromSeconds(3) }, states);

        runtime.Run(TimeSpan.FromSeconds(5), Speech);
        var result = await listen;

        Assert.Equal(ModuleSpeechInputEnding.MaximumDuration, result.Ending);
        Assert.Equal(TimeSpan.FromSeconds(2.9), states.Items.Last(state => state.Phase == ModuleSpeechInputPhase.Listening).Elapsed);
    }

    [Fact]
    public async Task TheMicrophoneSetupDoesNotCountAgainstTheMaximumDuration()
    {
        // Opening takes two seconds of the three the input may last.
        var runtime = new FakeRuntime { OpenDelay = TimeSpan.FromSeconds(2) };
        var states = new StateLog();
        var listen = new ModuleSpeechInput(runtime, _ => { }).ListenAsync(new() { MaximumDuration = TimeSpan.FromSeconds(3) }, states);

        runtime.Run(TimeSpan.FromSeconds(5), Speech);
        var result = await listen;

        Assert.Equal(ModuleSpeechInputEnding.MaximumDuration, result.Ending);
        var listening = states.Items.Where(state => state.Phase == ModuleSpeechInputPhase.Listening).ToArray();
        Assert.Equal(TimeSpan.Zero, listening[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(2.9), listening[^1].Elapsed);
    }

    [Fact]
    public async Task APolicyThatRefusesTheEngineWhileRecordingEndsTheInputAsARefusal()
    {
        var runtime = new FakeRuntime();
        var written = new List<string>();
        var listen = new ModuleSpeechInput(runtime, written.Add).ListenAsync();

        runtime.Run(TimeSpan.FromSeconds(1), Speech);
        runtime.Refused = "Sending audio to Cloud is turned off by your organization.";
        runtime.Run(Step, Speech);

        var error = await Assert.ThrowsAsync<ModuleSpeechInputException>(() => listen);
        Assert.Equal(ModuleSpeechInputErrorKind.Refused, error.Kind);
        Assert.Equal(runtime.Refused, error.Message);
        Assert.False(runtime.Capture!.Stopped);
        Assert.True(runtime.Capture.Disposed);
        Assert.Equal(0, runtime.Transcriptions);
        Assert.Equal("modules.speech-input.failed kind=refused", written[^1]);
    }

    [Fact]
    public async Task AStopBeforeTheMicrophoneOpenedEndsTheRecordingAtOnce()
    {
        var runtime = new FakeRuntime { OpenGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var input = new ModuleSpeechInput(runtime, _ => { });

        var listen = input.ListenAsync();
        input.Stop();
        runtime.OpenGate.SetResult();

        Assert.Equal(ModuleSpeechInputEnding.Stopped, (await listen).Ending);
    }

    [Fact]
    public async Task CancellationDiscardsTheRecording()
    {
        var runtime = new FakeRuntime();
        var written = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var listen = new ModuleSpeechInput(runtime, written.Add).ListenAsync(cancellationToken: cancellation.Token);

        runtime.Run(TimeSpan.FromSeconds(1), Speech);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
        Assert.False(runtime.Capture!.Stopped);
        Assert.True(runtime.Capture.Disposed);
        Assert.Equal(0, runtime.Transcriptions);
        Assert.Equal("modules.speech-input.canceled", written[^1]);
    }

    [Fact]
    public async Task CancellationDuringTheTranscriptionStaysACancellation()
    {
        var runtime = new FakeRuntime();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Transcribe = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new("never", null);
        };
        var input = new ModuleSpeechInput(runtime, _ => { });
        var listen = input.ListenAsync(cancellationToken: cancellation.Token);

        input.Stop();
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
        Assert.True(runtime.Capture!.Disposed);
    }

    [Fact]
    public async Task ALostMicrophoneEndsTheInputWithoutText()
    {
        var runtime = new FakeRuntime();
        var listen = new ModuleSpeechInput(runtime, _ => { }).ListenAsync();

        runtime.Run(TimeSpan.FromSeconds(0.5), Speech);
        runtime.Capture!.Recording = false;
        runtime.Run(Step, 0);

        var error = await Assert.ThrowsAsync<ModuleSpeechInputException>(() => listen);
        Assert.Equal(ModuleSpeechInputErrorKind.MicrophoneUnavailable, error.Kind);
        Assert.True(runtime.Capture.Disposed);
        Assert.Equal(0, runtime.Transcriptions);
    }

    [Fact]
    public async Task OnlyOneInputRunsAtATime()
    {
        var runtime = new FakeRuntime();
        var written = new List<string>();
        var input = new ModuleSpeechInput(runtime, written.Add);
        var first = input.ListenAsync();

        var second = await Assert.ThrowsAsync<ModuleSpeechInputException>(() => input.ListenAsync());
        input.Stop();

        Assert.Equal(ModuleSpeechInputErrorKind.Busy, second.Kind);
        Assert.Equal("hello world", (await first).Text);
        Assert.Contains("modules.speech-input.failed kind=busy", written);
        // Once the first has ended, the next can start.
        var third = input.ListenAsync();
        input.Stop();
        await third;
    }

    [Fact]
    public async Task TheHostsRefusalsReachTheModuleUnchanged()
    {
        foreach (var kind in new[] { ModuleSpeechInputErrorKind.Busy, ModuleSpeechInputErrorKind.Refused, ModuleSpeechInputErrorKind.NotSetUp,
            ModuleSpeechInputErrorKind.MicrophoneUnavailable })
        {
            var runtime = new FakeRuntime { OpenError = new ModuleSpeechInputException(kind, "Reason " + kind) };
            var error = await Assert.ThrowsAsync<ModuleSpeechInputException>(() => new ModuleSpeechInput(runtime, _ => { }).ListenAsync());
            Assert.Equal(kind, error.Kind);
            Assert.Equal("Reason " + kind, error.Message);
        }
    }

    [Fact]
    public async Task AQuietRecordingIsNotTranscribed()
    {
        var runtime = new FakeRuntime { Peak = 0.001f };
        var input = new ModuleSpeechInput(runtime, _ => { });
        var listen = input.ListenAsync();
        input.Stop();

        var error = await Assert.ThrowsAsync<ModuleSpeechInputException>(() => listen);
        Assert.Equal(ModuleSpeechInputErrorKind.NoSpeech, error.Kind);
        Assert.Equal(0, runtime.Transcriptions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyTranscriptIsNoSpeech(string text)
    {
        var runtime = new FakeRuntime { Transcribe = (_, _) => Task.FromResult(new SpeechInputTranscript(text, "en")) };
        var input = new ModuleSpeechInput(runtime, _ => { });
        var listen = input.ListenAsync();
        input.Stop();

        Assert.Equal(ModuleSpeechInputErrorKind.NoSpeech, (await Assert.ThrowsAsync<ModuleSpeechInputException>(() => listen)).Kind);
    }

    [Fact]
    public async Task AHostCancellationIsARefusalWhileThePolicyRefusesTheEngine()
    {
        var runtime = new FakeRuntime();
        var listen = new ModuleSpeechInput(runtime, _ => { }).ListenAsync();

        runtime.Refused = "Sending audio to Cloud is turned off by your organization.";
        runtime.Host.Cancel();

        var error = await Assert.ThrowsAsync<ModuleSpeechInputException>(() => listen);
        Assert.Equal(ModuleSpeechInputErrorKind.Refused, error.Kind);
        Assert.Equal(runtime.Refused, error.Message);
    }

    [Fact]
    public async Task AHostCancellationAtShutdownIsAFailure()
    {
        var runtime = new FakeRuntime();
        var listen = new ModuleSpeechInput(runtime, _ => { }).ListenAsync();

        runtime.Closing = true;
        runtime.Host.Cancel();

        Assert.Equal(ModuleSpeechInputErrorKind.Failed, (await Assert.ThrowsAsync<ModuleSpeechInputException>(() => listen)).Kind);
        Assert.True(runtime.Capture!.Disposed);
    }

    [Fact]
    public void ErrorsMapToTheirKinds()
    {
        static ModuleSpeechInputErrorKind KindOf(Exception error, Func<string?>? refusal = null, bool closing = false) =>
            Assert.IsType<ModuleSpeechInputException>(ModuleSpeechInput.ModuleError(error, CancellationToken.None, refusal ?? (() => null), closing)).Kind;

        Assert.Equal(ModuleSpeechInputErrorKind.Refused, KindOf(new ManagedPolicyDeniedException("transcription", "No.")));
        Assert.Equal(ModuleSpeechInputErrorKind.Refused, KindOf(new InvalidOperationException("Wrapped", new ManagedPolicyDeniedException("transcription", "No."))));
        Assert.Equal(ModuleSpeechInputErrorKind.Refused, KindOf(new OperationCanceledException(), () => "No."));
        Assert.Equal(ModuleSpeechInputErrorKind.Failed, KindOf(new OperationCanceledException(), closing: true));
        Assert.Equal(ModuleSpeechInputErrorKind.NotSetUp, KindOf(new PluginRequestException("Key missing.", PluginRequestFailureKind.Authentication)));
        Assert.Equal(ModuleSpeechInputErrorKind.Failed, KindOf(new PluginRequestException("Server error.", PluginRequestFailureKind.ServerError)));
        Assert.Equal(ModuleSpeechInputErrorKind.Failed, KindOf(new IOException("Disk.")));
        Assert.Equal(ModuleSpeechInputErrorKind.Busy, KindOf(new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Busy)));
        Assert.Equal("No.", ModuleSpeechInput.ModuleError(new ManagedPolicyDeniedException("transcription", "No."), CancellationToken.None, () => null, false).Message);

        // The module's own cancellation stays a cancellation, whatever the host reported.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(ModuleSpeechInput.ModuleError(new IOException("Late."), cancellation.Token, () => "No.", false));
    }

    [Fact]
    public void OptionsHaveDefaultsAndRanges()
    {
        Assert.Equal(((TimeSpan?)TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)), ModuleSpeechInput.Validate(null));
        Assert.Equal(((TimeSpan?)null, TimeSpan.FromSeconds(10)),
            ModuleSpeechInput.Validate(new() { SilenceTimeout = null, MaximumDuration = TimeSpan.FromSeconds(10) }));
        Assert.Equal(((TimeSpan?)ModuleSpeechInputOptions.MinimumSilenceTimeout, ModuleSpeechInputOptions.LongestDuration),
            ModuleSpeechInput.Validate(new() { SilenceTimeout = ModuleSpeechInputOptions.MinimumSilenceTimeout, MaximumDuration = ModuleSpeechInputOptions.LongestDuration }));

        foreach (var invalid in new ModuleSpeechInputOptions[]
        {
            new() { SilenceTimeout = TimeSpan.Zero },
            new() { SilenceTimeout = TimeSpan.FromSeconds(0.4) },
            new() { SilenceTimeout = TimeSpan.FromSeconds(31) },
            new() { MaximumDuration = TimeSpan.FromSeconds(0.5) },
            new() { MaximumDuration = TimeSpan.FromMinutes(6) },
            new() { MaximumDuration = TimeSpan.FromSeconds(-1) }
        })
            Assert.Throws<ArgumentOutOfRangeException>(() => ModuleSpeechInput.Validate(invalid));
    }

    [Fact]
    public async Task InvalidOptionsAreRejectedBeforeTheMicrophoneOpens()
    {
        var runtime = new FakeRuntime();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new ModuleSpeechInput(runtime, _ => { }).ListenAsync(new() { MaximumDuration = TimeSpan.Zero }));
        Assert.Null(runtime.Capture);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(-1f, 0)]
    [InlineData(float.NaN, 0)]
    [InlineData(0.001f, 0)]
    [InlineData(0.0001f, 0)]
    [InlineData(1f, 1)]
    [InlineData(2f, 1)]
    public void LoudnessBecomesAMeterLevel(float rms, double level) => Assert.Equal(level, ModuleSpeechInput.Loudness(rms), 3);

    [Fact]
    public void LoudnessRisesWithTheSignal() =>
        Assert.True(ModuleSpeechInput.Loudness(0.01f) < ModuleSpeechInput.Loudness(0.1f) && ModuleSpeechInput.Loudness(0.0316f) is > 0.49 and < 0.51);

    [Fact]
    public async Task TheDictionaryCorrectsTheTextButSnippetsDoNotExpand()
    {
        var root = Path.Join(Path.GetTempPath(), "speech-input-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var dictionaryPath = Path.Join(root, "dictionary.json");
            var snippetPath = Path.Join(root, "snippets.json");
            File.WriteAllText(snippetPath, JsonSerializer.Serialize(new[] { new Snippet { Id = "signature", Trigger = "sig", Replacement = "Expanded" } }));
            File.WriteAllText(dictionaryPath, JsonSerializer.Serialize(new[]
            {
                new DictionaryEntry { Id = "fix", EntryType = DictionaryEntryType.Correction, Original = "mistake", Replacement = "correct" }
            }));
            // The same catalogs a dictation would expand the snippet from.
            var lexicon = DictationLexiconSnapshot.Load(dictionaryPath, snippetPath);
            Assert.Equal("Expanded correct", (await lexicon.ProcessAsync("sig mistake", new() { TranscriptionNumberNormalizationEnabled = false }, "en", "en",
                false, _ => Task.FromResult(""), CancellationToken.None, TypeWhisper.Core.Interfaces.TranscriptionTask.Transcribe, null, "engine", "model")).Text);

            var text = await ModuleSpeechInput.ProcessTextAsync("sig mistake", lexicon.Dictionary, new() { TranscriptionNumberNormalizationEnabled = false },
                "en", "en", false, "engine", "model", CancellationToken.None);

            Assert.Equal("sig correct", text);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StateLog : IProgress<ModuleSpeechInputState>
    {
        private readonly List<ModuleSpeechInputState> _items = [];
        private int _undispatched;
        internal IReadOnlyList<ModuleSpeechInputState> Items { get { lock (_items) return _items.ToArray(); } }
        internal int Undispatched => Volatile.Read(ref _undispatched);
        public void Report(ModuleSpeechInputState value)
        {
            if (!FakeRuntime.Dispatching) Interlocked.Increment(ref _undispatched);
            lock (_items) _items.Add(value);
        }
    }

    // A microphone and clock the test drives: each step advances the clock, reports a level and ticks.
    private sealed class FakeRuntime : ISpeechInputRuntime
    {
        private Action<float>? _level;
        private volatile Action? _tick;
        public TimeSpan Now { get; private set; }
        public bool Closing { get; set; }
        public string? Refused { get; set; }
        public CancellationTokenSource Host { get; } = new();
        public Exception? OpenError { get; init; }
        public TaskCompletionSource? OpenGate { get; init; }
        public TimeSpan OpenDelay { get; init; }
        public float Peak { get; init; } = 0.1f;
        public FakeCapture? Capture { get; private set; }
        public int Transcriptions;
        public Func<float[], CancellationToken, Task<SpeechInputTranscript>> Transcribe { get; set; } =
            (_, _) => Task.FromResult(new SpeechInputTranscript(" hello world ", "en"));

        public string? Refusal() => Refused;

        public async Task<ISpeechInputCapture> OpenAsync(Action<float> level, CancellationToken cancellationToken)
        {
            if (OpenGate is not null) await OpenGate.Task;
            Now += OpenDelay;
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenError is not null) throw OpenError;
            _level = level;
            return Capture = new FakeCapture(this, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Host.Token).Token);
        }

        // Stands in for the UI thread: a report counts as dispatched while its action runs on this thread.
        [ThreadStatic] private static bool _dispatching;
        internal static bool Dispatching => _dispatching;

        public void Dispatch(Action action)
        {
            _dispatching = true;
            try { action(); }
            finally { _dispatching = false; }
        }

        public IDisposable Every(TimeSpan interval, Action tick)
        {
            Assert.Equal(Step, interval);
            _tick = tick;
            return new Release(() => _tick = null);
        }

        internal void Run(TimeSpan duration, float level)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Step)
            {
                Now += Step;
                if (Capture is { Recording: true }) Capture.Level = level;
                _level?.Invoke(level);
                _tick?.Invoke();
            }
        }

        private sealed class Release(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }

    private sealed class FakeCapture(FakeRuntime runtime, CancellationToken cancellation) : ISpeechInputCapture
    {
        public CancellationToken Cancellation => cancellation;
        public bool Recording { get; set; } = true;
        public float Level { get; set; }
        public bool RecognizeQuietClips => false;
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public Task<SpeechInputAudio> StopAsync()
        {
            Stopped = true;
            Recording = false;
            return Task.FromResult(new SpeechInputAudio(new float[ModuleSpeechInput.SampleRate], runtime.Peak));
        }

        public Task<SpeechInputTranscript> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runtime.Transcriptions);
            return runtime.Transcribe(samples, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
