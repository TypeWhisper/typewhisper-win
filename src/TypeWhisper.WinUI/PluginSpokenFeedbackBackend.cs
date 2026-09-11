using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class PluginSpokenFeedbackBackend(PortablePluginRuntimeRegistry runtime, ISpokenFeedbackBackend windows) : ISpokenFeedbackBackend
{
    private const string Prefix = "plugin:";
    public IReadOnlyList<SpokenFeedbackVoice> GetVoices()
    {
        IReadOnlyList<SpokenFeedbackVoice> local;
        try { local = windows.GetVoices(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Windows voice enumeration failed: " + ex.GetType().Name);
            local = [];
        }
        return local.Concat(runtime.TtsProviders.Where(p => p.Ready).SelectMany(p => p.Voices.Select(v => new SpokenFeedbackVoice(
            Prefix + Uri.EscapeDataString(p.PluginId) + ":" + Uri.EscapeDataString(v.Id),
            p.Name + " · " + v.DisplayName + " (cloud)")))).ToArray();
    }

    public async Task SpeakAsync(SpokenFeedbackRequest request, CancellationToken ct)
    {
        if (request.VoiceId?.StartsWith(Prefix, StringComparison.Ordinal) != true)
        { await windows.SpeakAsync(request, ct); return; }
        var parts = request.VoiceId[Prefix.Length..].Split(':');
        if (parts.Length != 2) throw new InvalidOperationException("The saved speech voice is invalid.");
        await runtime.SpeakAsync(Uri.UnescapeDataString(parts[0]), new(request.Text, request.Language, TtsPurpose.Status)
        { VoiceId = Uri.UnescapeDataString(parts[1]), OutputDeviceId = request.OutputDeviceId }, ct);
    }
}
