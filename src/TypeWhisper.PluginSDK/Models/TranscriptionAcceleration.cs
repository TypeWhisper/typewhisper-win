namespace TypeWhisper.PluginSDK.Models;

public enum TranscriptionAccelerationPreference
{
    Auto,
    Cpu,
    NvidiaCuda,
    AmdVulkan,
    AmdRocm
}

public enum TranscriptionAccelerationBackend
{
    Cpu,
    NvidiaCuda,
    AmdVulkan,
    AmdRocm
}

public sealed record TranscriptionAccelerationStatus(
    TranscriptionAccelerationBackend ActiveBackend,
    string DisplayText,
    string? Detail = null,
    bool RequiresRestart = false);

