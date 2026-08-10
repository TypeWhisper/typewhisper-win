namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Represents a support-oriented snapshot of a transcription engine's acceleration state.
/// </summary>
/// <param name="EngineId">Stable transcription engine identifier.</param>
/// <param name="EngineName">Human-readable transcription engine name.</param>
/// <param name="SelectedPreference">Acceleration preference selected in the host.</param>
/// <param name="ActiveBackend">Backend currently reported as active.</param>
/// <param name="RuntimePath">Loaded or attempted native runtime path when known.</param>
/// <param name="LastNativeError">Most recent native runtime failure message when available.</param>
public sealed record TranscriptionAccelerationDiagnostics(
    string EngineId,
    string EngineName,
    TranscriptionAccelerationPreference SelectedPreference,
    TranscriptionAccelerationBackend ActiveBackend,
    string? RuntimePath = null,
    string? LastNativeError = null);
