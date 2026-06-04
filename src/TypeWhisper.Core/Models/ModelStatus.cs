namespace TypeWhisper.Core.Models;

/// <summary>
/// Lists the supported model status type values.
/// </summary>
public enum ModelStatusType
{
    /// <summary>
    /// Represents the not downloaded option.
    /// </summary>
    NotDownloaded,
    /// <summary>
    /// Represents the downloading option.
    /// </summary>
    Downloading,
    /// <summary>
    /// Represents the loading option.
    /// </summary>
    Loading,
    /// <summary>
    /// Represents the ready option.
    /// </summary>
    Ready,
    /// <summary>
    /// Represents the error option.
    /// </summary>
    Error
}

/// <summary>
/// Represents model status data.
/// </summary>
public sealed record ModelStatus
{
    /// <summary>
    /// Gets or sets the type value.
    /// </summary>
    public required ModelStatusType Type { get; init; }
    /// <summary>
    /// Gets or sets the progress value.
    /// </summary>
    public double Progress { get; init; }
    /// <summary>
    /// Gets or sets the bytes per second value.
    /// </summary>
    public double? BytesPerSecond { get; init; }
    /// <summary>
    /// Gets or sets the error message value.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static ModelStatus NotDownloaded => new() { Type = ModelStatusType.NotDownloaded };
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static ModelStatus Ready => new() { Type = ModelStatusType.Ready };
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static ModelStatus LoadingModel => new() { Type = ModelStatusType.Loading };

    /// <summary>
    /// Performs downloading model.
    /// </summary>
    public static ModelStatus DownloadingModel(double progress, double? bytesPerSecond = null) =>
        new() { Type = ModelStatusType.Downloading, Progress = progress, BytesPerSecond = bytesPerSecond };

    /// <summary>
    /// Creates a failed status.
    /// </summary>
    public static ModelStatus Failed(string message) =>
        new() { Type = ModelStatusType.Error, ErrorMessage = message };
}
