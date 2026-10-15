using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // The registry enforces policy for its roles. The local Parakeet path never touches the registry, so its
    // checks live here; its engine reports the sherpa-onnx package as its selection ID.
    private static ManagedProviderPolicy ProviderPolicy => ManagedProviderPolicy.From(WinUIManagedPolicy.Current);

    private static string? LocalTranscriptionRefusal(string? modelId = null) =>
        ProviderPolicy.TranscriptionRefusal(LocalTranscriptionPlugin.PluginId, LocalTranscriptionPlugin.PluginId, ContentLocality.Local, modelId);

    /// <summary>Why policy refuses the active transcription provider and model before audio is captured, or null.</summary>
    internal string? TranscriptionPolicyRefusal()
    {
        if (!UsesRegistryProvider) return LocalTranscriptionRefusal(Models.ActiveModelId);
        if (ActiveRegistryProvider is { } provider) return PluginRuntime.TranscriptionRefusal(provider.SelectionId, provider.SelectedModelId);
        // The registry knows no such role: policy may require a provider that is not installed or enabled.
        return EnforcedTranscription is { } pair && SessionProviderId(pair.ProviderId) == _providerId
            ? Loc.T("The transcription provider {0} is not installed or enabled.", pair.ProviderId) : null;
    }

    private void DenyLocalTranscriptionIfRefused()
    {
        if (LocalTranscriptionRefusal(Models.ActiveModelId) is { } refused) throw new ManagedPolicyDeniedException("transcription", refused);
    }

    /// <summary>Why policy refuses downloading models or runtime assets, or null.</summary>
    internal static string? ModelDownloadRefusal() => ProviderPolicy.ModelDownloadRefusal();
}
