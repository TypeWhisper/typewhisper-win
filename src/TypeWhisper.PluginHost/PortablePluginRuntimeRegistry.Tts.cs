using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>Voices from an enabled package supporting request-specific playback selection.</summary>
public sealed record PortableTtsProvider(string PluginId, string Name, bool Ready, IReadOnlyList<PluginVoiceInfo> Voices);

public sealed partial class PortablePluginRuntimeRegistry
{
    /// <summary>Snapshot only; listing voices never invokes speech synthesis.</summary>
    public IReadOnlyList<PortableTtsProvider> TtsProviders
    {
        get { lock (_sync) return _index.TtsSnapshots.Where(item => _slots[item.PluginId].Accepting).ToArray(); }
    }

    /// <summary>Retains the package until playback has completed or cancellation has drained it.</summary>
    public Task<bool> SpeakAsync(string pluginId, TtsSpeakRequest request, CancellationToken ct) =>
        UseConfigurationAsync(pluginId, async (plugin, token) =>
        {
            if (plugin is not ITtsProviderPlugin { IsConfigured: true, SupportsPlaybackSelection: true } provider)
                throw new InvalidOperationException("The selected speech provider is unavailable.");
            var playback = await provider.SpeakAsync(request, token);
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
                try { playback.Stop(); }
                finally { (playback as IDisposable)?.Dispose(); }
            }
        }, ct);
}
