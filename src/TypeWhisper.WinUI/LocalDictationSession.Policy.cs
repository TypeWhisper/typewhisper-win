using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // The registry enforces policy for its roles. The local Parakeet path never touches the registry, so its
    // checks live here; its engine reports the sherpa-onnx package as its selection ID.
    private static ManagedProviderPolicy ProviderPolicy => ManagedProviderPolicy.From(WinUIManagedPolicy.Current);

    private static string? LocalTranscriptionRefusal() =>
        ProviderPolicy.TranscriptionRefusal(LocalTranscriptionPlugin.PluginId, LocalTranscriptionPlugin.PluginId, ContentLocality.Local);

    /// <summary>Why policy refuses the active transcription provider before audio is captured, or null.</summary>
    internal string? TranscriptionPolicyRefusal() => UsesRegistryProvider
        ? PluginRuntime.TranscriptionRefusal(RegistrySelectionId(_providerId))
        : LocalTranscriptionRefusal();

    private static void DenyLocalTranscriptionIfRefused()
    {
        if (LocalTranscriptionRefusal() is { } refused) throw new ManagedPolicyDeniedException("transcription", refused);
    }

    /// <summary>Why policy refuses downloading models or runtime assets, or null.</summary>
    internal static string? ModelDownloadRefusal() => ProviderPolicy.ModelDownloadRefusal();
}
