namespace TypeWhisper.WinUI;

// Overlay and capture tools (Steam, OBS, graphics driver utilities) register implicit Vulkan layers
// that the loader injects into every Vulkan process, where they can crash or slow ggml's Vulkan backend.
// Turning them off here also covers the transcription worker and CrispASR, which inherit this environment.
// Loaders before 1.3.234 ignore the variable. A user-set VK_LOADER_LAYERS_DISABLE or TYPEWHISPER_KEEP_VULKAN_IMPLICIT_LAYERS=1 keeps the layers.
internal static class VulkanLayerPolicy
{
    internal const string LoaderVariable = "VK_LOADER_LAYERS_DISABLE";
    internal const string KeepVariable = "TYPEWHISPER_KEEP_VULKAN_IMPLICIT_LAYERS";
    internal const string DisableImplicitLayers = "~implicit~";

    internal static void Apply() => Apply(Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);

    internal static bool Apply(Func<string, string?> read, Action<string, string?> write)
    {
        if (read(LoaderVariable) is not null || read(KeepVariable) == "1") return false;
        write(LoaderVariable, DisableImplicitLayers);
        return true;
    }
}
