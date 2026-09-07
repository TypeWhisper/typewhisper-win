using System.Speech.Synthesis;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Local Windows voice output. No profile writes, default-device fallbacks or UI callbacks occur here.
internal sealed class WindowsSystemVoiceBackend : ISpokenFeedbackBackend
{
    public IReadOnlyList<SpokenFeedbackVoice> GetVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices().Where(voice => voice.Enabled)
            .Select(voice => new SpokenFeedbackVoice(voice.VoiceInfo.Name, voice.VoiceInfo.Name, voice.VoiceInfo.Culture.Name))
            .OrderBy(voice => voice.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public Task SpeakAsync(SpokenFeedbackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("There is no text to read.", nameof(request));
        if (request.Text.Length > SpokenFeedbackRequest.MaxTextLength)
            throw new ArgumentException("Spoken feedback is limited to 4,000 characters; no text was read.", nameof(request));
        // SAPI and NAudio capture synchronization contexts. Create them off the UI thread.
        return Task.Run(() => SpeakCoreAsync(request, cancellationToken), cancellationToken);
    }

    private static async Task SpeakCoreAsync(SpokenFeedbackRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var wave = new BoundedSpeechWaveStream(SpokenFeedbackRequest.MaxAudioBytes);
        using (var synthesizer = new SpeechSynthesizer())
        {
            if (!string.IsNullOrEmpty(request.VoiceId))
            {
                if (!synthesizer.GetInstalledVoices().Any(voice => voice.Enabled && voice.VoiceInfo.Name == request.VoiceId))
                    throw new InvalidOperationException("The saved Windows voice is unavailable. Choose an installed voice in Audio settings.");
                synthesizer.SelectVoice(request.VoiceId);
            }
            synthesizer.SetOutputToWaveStream(wave);
            var completed = new TaskCompletionSource<SpeakCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<SpeakCompletedEventArgs> finished = (_, args) => completed.TrySetResult(args);
            synthesizer.SpeakCompleted += finished;
            try
            {
                ct.ThrowIfCancellationRequested();
                synthesizer.SpeakAsync(request.Text);
                Exception? cancellationError = null;
                await using (ct.Register(() =>
                {
                    try { synthesizer.SpeakAsyncCancelAll(); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref cancellationError, ex, null); }
                }))
                {
                    var result = await completed.Task.ConfigureAwait(false);
                    if (cancellationError is not null) throw new InvalidOperationException("Windows voice cancellation failed after synthesis drained.", cancellationError);
                    ct.ThrowIfCancellationRequested();
                    if (result.Error is not null) throw new InvalidOperationException("Windows could not synthesize this text.", result.Error);
                    if (result.Cancelled) throw new OperationCanceledException("Windows voice synthesis was canceled.", ct);
                }
            }
            finally { synthesizer.SpeakCompleted -= finished; }
        }
        ct.ThrowIfCancellationRequested();
        wave.Position = 0;
        using var reader = new WaveFileReader(wave);
        if (reader.TotalTime.TotalSeconds is <= 0 || !double.IsFinite(reader.TotalTime.TotalSeconds)
            || reader.TotalTime.TotalSeconds > SpokenFeedbackRequest.MaxAudioSeconds)
            throw new InvalidOperationException("The synthesized speech exceeds the two-minute limit; no audio was played.");
        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrEmpty(request.OutputDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(request.OutputDeviceId);
        if (device.State != DeviceState.Active)
            throw new InvalidOperationException("The saved audio output is unavailable. Choose an active output in Audio settings.");
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> playbackStopped = (_, args) => stopped.TrySetResult(args);
        output.PlaybackStopped += playbackStopped;
        try
        {
            output.Init(reader);
            ct.ThrowIfCancellationRequested();
            output.Play();
            Exception? stopError = null;
            await using (ct.Register(() =>
            {
                try { output.Stop(); }
                catch (Exception ex) { Interlocked.CompareExchange(ref stopError, ex, null); }
            }))
            {
                var result = await stopped.Task.ConfigureAwait(false);
                if (stopError is not null) throw new InvalidOperationException("Audio cancellation failed after playback drained.", stopError);
                ct.ThrowIfCancellationRequested();
                if (result.Exception is not null) throw new InvalidOperationException("Spoken feedback playback failed.", result.Exception);
            }
        }
        finally { output.PlaybackStopped -= playbackStopped; }
    }
}
