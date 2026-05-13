namespace TypeWhisper.PluginSDK.Models;

public enum TranscriptionAccelerationPreference
{
    Auto,
    Cpu,
    NvidiaCuda
}

public enum TranscriptionAccelerationBackend
{
    Cpu,
    NvidiaCuda
}

public sealed record TranscriptionAccelerationStatus(
    TranscriptionAccelerationBackend ActiveBackend,
    string DisplayText,
    string? Detail = null,
    bool RequiresRestart = false);

