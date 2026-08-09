using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional capability for transcription engines that expose support-oriented acceleration diagnostics.
/// </summary>
public interface ITranscriptionAccelerationDiagnosticsProvider
{
    /// <summary>
    /// Gets the current acceleration diagnostic snapshot.
    /// </summary>
    TranscriptionAccelerationDiagnostics AccelerationDiagnostics { get; }
}
