namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Lists the supported transcription acceleration preference values.
/// </summary>
public enum TranscriptionAccelerationPreference
{
    /// <summary>
    /// Represents the auto option.
    /// </summary>
    Auto,
    /// <summary>
    /// Represents the CPU option.
    /// </summary>
    Cpu,
    /// <summary>
    /// Represents the nvidia CUDA option.
    /// </summary>
    NvidiaCuda,
    /// <summary>
    /// Represents the amd vulkan option.
    /// </summary>
    AmdVulkan,
    /// <summary>
    /// Represents the amd rocm option.
    /// </summary>
    AmdRocm
}

/// <summary>
/// Lists the supported transcription acceleration backend values.
/// </summary>
public enum TranscriptionAccelerationBackend
{
    /// <summary>
    /// Represents the CPU option.
    /// </summary>
    Cpu,
    /// <summary>
    /// Represents the nvidia CUDA option.
    /// </summary>
    NvidiaCuda,
    /// <summary>
    /// Represents the amd vulkan option.
    /// </summary>
    AmdVulkan,
    /// <summary>
    /// Represents the amd rocm option.
    /// </summary>
    AmdRocm
}

/// <summary>
/// Represents transcription acceleration status data.
/// </summary>
/// <param name="ActiveBackend">Active backend supplied to the member.</param>
/// <param name="DisplayText">Display text supplied to the member.</param>
/// <param name="Detail">Detail supplied to the member.</param>
/// <param name="RequiresRestart">Requires restart supplied to the member.</param>
public sealed record TranscriptionAccelerationStatus(
    TranscriptionAccelerationBackend ActiveBackend,
    string DisplayText,
    string? Detail = null,
    bool RequiresRestart = false);

