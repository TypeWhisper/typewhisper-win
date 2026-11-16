using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>Voices from an enabled package supporting request-specific playback selection.</summary>
public sealed record PortableTtsProvider(string PluginId, string Name, bool Ready, IReadOnlyList<PluginVoiceInfo> Voices)
{
    /// <summary>Whether synthesis runs locally, as declared by the package manifest.</summary>
    public bool IsLocal { get; init; }
}

public sealed partial class PortablePluginRuntimeRegistry
{
    /// <summary>Snapshot only; listing voices never invokes speech synthesis.</summary>
    public IReadOnlyList<PortableTtsProvider> TtsProviders
    {
        get { lock (_sync) return _index.TtsSnapshots.Where(item => _slots[item.PluginId].Accepting).ToArray(); }
    }

    /// <summary>Retains the package until playback has completed or cancellation has drained it.</summary>
    public Task<bool> SpeakAsync(string pluginId, TtsSpeakRequest request, CancellationToken ct)
    {
        // Spoken feedback carries transcript text, so remote synthesis follows the text-processing policy.
        if (TextTransferRefusal(pluginId) is { } refused) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("tts", refused);
        return RefusedWhenPolicyCancelsAsync(UseConfigurationAsync(pluginId, async (plugin, token) =>
        {
            // The policy may have changed while this request waited for the runtime gate.
            if (TextTransferRefusal(pluginId) is { } refusedNow) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("tts", refusedNow);
            if (plugin is not ITtsProviderPlugin { IsConfigured: true, SupportsPlaybackSelection: true } provider)
                throw new InvalidOperationException("The selected speech provider is unavailable.");
            var playback = await provider.SpeakAsync(request, token);
            // Disposed once the finally below has stopped the playback, also when stopping throws.
            using var disposablePlayback = playback as IDisposable;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(object? sender, EventArgs args) => completion.TrySetResult();
            try
            {
                playback.Completed += Completed;
                if (!playback.IsActive) completion.TrySetResult();
                using var registration = token.Register(() => completion.TrySetCanceled(token));
                await completion.Task;
                if (playback.Error is not null) throw new InvalidOperationException(playback.Error);
                return true;
            }
            finally
            {
                playback.Completed -= Completed;
                playback.Stop();
            }
        }, ct), () =>
        {
            // A cancellation the policy pass caused is reported as the refusal, not as a playback failure.
            if (TextTransferRefusal(pluginId) is { } refusedMeanwhile) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("tts", refusedMeanwhile);
        }, ct);
    }
}
