namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Context information passed to post-processing plugins.
/// </summary>
public sealed record PostProcessingContext
{
    /// <summary>Detected or configured source language (ISO code), or null.</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>Display name of the active foreground application, or null.</summary>
    public string? ActiveAppName { get; init; }

    /// <summary>Process name of the active foreground application, or null.</summary>
    public string? ActiveAppProcessName { get; init; }

    /// <summary>Name of the active dictation profile, or null.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Duration of the source audio in seconds.</summary>
    public double AudioDurationSeconds { get; init; }
}
