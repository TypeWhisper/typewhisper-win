using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

// The plugin runtime's diarization operations IAudioDiarization needs, so the mapping and policy translation run
// without a plugin registry in tests.
internal sealed record ModuleDiarizationRuntime(
    Func<IReadOnlyList<ModuleDiarizationProvider>> Providers,
    Action<Action> Subscribe,
    Action<Action> Unsubscribe,
    Func<string, IProgress<double>?, CancellationToken, Task> Prepare,
    Func<string, PluginDiarizationRequest, IProgress<double>?, CancellationToken, Task<PluginDiarizationResult>> Diarize,
    Func<string, CancellationToken, Task<float[]>> Decode);

// IAudioDiarization over the plugin runtime: decodes the file to 16 kHz mono, lets the runtime judge the policy and
// run the provider, and turns the provider's labels into S1, S2, … in the order the speakers first speak.
internal sealed class ModuleAudioDiarization : IAudioDiarization
{
    internal const int SampleRate = 16000;
    private readonly ModuleDiarizationRuntime _runtime;
    private readonly Action<Action> _post;
    private readonly Action<string, Exception> _report;

    internal ModuleAudioDiarization(ModuleDiarizationRuntime runtime, Action<Action> post, Action<string, Exception> report)
    {
        _runtime = runtime;
        _post = post;
        _report = report;
    }

    public IReadOnlyList<ModuleDiarizationProvider> Providers => _runtime.Providers();

    public IDisposable ObserveChanges(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var observation = new ModuleObservation(_post, _report, "modules.diarization.observer-failed");
        Action listener = () => observation.Deliver(handler);
        _runtime.Subscribe(listener);
        observation.Unsubscribe = () => _runtime.Unsubscribe(listener);
        return observation;
    }

    public async Task PrepareAsync(string providerId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        try { await _runtime.Prepare(providerId, progress, cancellationToken).ConfigureAwait(false); }
        catch (ManagedPolicyDeniedException refused) { throw Refused(refused); }
    }

    public async Task<IReadOnlyList<ModuleSpeakerTurn>> DiarizeAsync(string providerId, string audioFilePath,
        ModuleDiarizationOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        var provider = _runtime.Providers().FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The diarization provider is unavailable.");
        if (!provider.IsReady) throw new InvalidOperationException("The diarization provider is not ready. Install its models first.");
        if (options?.SpeakerCount is { } count && (provider.MinimumSpeakerCount is not { } minimum || provider.MaximumSpeakerCount is not { } maximum
            || count < minimum || count > maximum))
            throw new ArgumentOutOfRangeException(nameof(options), count, "The diarization provider does not accept this speaker count.");
        var samples = await _runtime.Decode(audioFilePath, cancellationToken).ConfigureAwait(false);
        var request = new PluginDiarizationRequest(samples, SampleRate)
            { AudioPath = audioFilePath, Language = options?.Language, SpeakerCount = options?.SpeakerCount };
        try
        {
            var result = await _runtime.Diarize(provider.Id, request, progress, cancellationToken).ConfigureAwait(false);
            return Turns(result.Turns, request.DurationSeconds);
        }
        catch (ManagedPolicyDeniedException refused) { throw Refused(refused); }
    }

    internal static ModulePolicyRefusedException Refused(ManagedPolicyDeniedException refused) => new(refused.Message, refused);

    // Drops turns without a valid span, keeps them inside the audio, sorts them by start and names the speakers in the
    // order they first speak, so labels are stable whatever the provider calls its speakers.
    internal static IReadOnlyList<ModuleSpeakerTurn> Turns(IEnumerable<PluginSpeakerTurn> turns, double durationSeconds)
    {
        var end = double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : double.MaxValue;
        var valid = turns
            .Where(turn => turn is not null && !string.IsNullOrWhiteSpace(turn.SpeakerLabel) && double.IsFinite(turn.Start) && double.IsFinite(turn.End))
            .Select(turn => (turn.SpeakerLabel, Start: Math.Max(0, turn.Start), End: Math.Min(end, turn.End), turn.Confidence))
            .Where(turn => turn.End > turn.Start)
            .OrderBy(turn => turn.Start).ThenBy(turn => turn.End).ThenBy(turn => turn.SpeakerLabel, StringComparer.Ordinal)
            .ToArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        return valid.Select(turn =>
        {
            if (!names.TryGetValue(turn.SpeakerLabel, out var name)) names.Add(turn.SpeakerLabel, name = "S" + (names.Count + 1));
            double? confidence = turn.Confidence is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : null;
            return new ModuleSpeakerTurn(name, turn.Start, turn.End, confidence);
        }).ToArray();
    }
}
