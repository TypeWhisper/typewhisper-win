namespace TypeWhisper.Core.Models;

public enum ModelStatusType
{
    NotDownloaded,
    Downloading,
    Loading,
    Ready,
    Error
}

public sealed record ModelStatus
{
    public required ModelStatusType Type { get; init; }
    public double Progress { get; init; }
    public double? BytesPerSecond { get; init; }
    public string? ErrorMessage { get; init; }

    public static ModelStatus NotDownloaded => new() { Type = ModelStatusType.NotDownloaded };
    public static ModelStatus Ready => new() { Type = ModelStatusType.Ready };
    public static ModelStatus LoadingModel => new() { Type = ModelStatusType.Loading };

    public static ModelStatus DownloadingModel(double progress, double? bytesPerSecond = null) =>
        new() { Type = ModelStatusType.Downloading, Progress = progress, BytesPerSecond = bytesPerSecond };

    public static ModelStatus Failed(string message) =>
        new() { Type = ModelStatusType.Error, ErrorMessage = message };
}
