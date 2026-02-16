namespace TypeWhisper.Core.Models;

public sealed record ModelFileInfo(string FileName, string DownloadUrl, int EstimatedSizeMB);

public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string SizeDescription { get; init; }
    public required int EstimatedSizeMB { get; init; }
    public required IReadOnlyList<ModelFileInfo> Files { get; init; }
    public string? SubDirectory { get; init; }
    public int LanguageCount { get; init; } = 25;
    public bool SupportsTranslation { get; init; }
    public bool IsRecommended { get; init; }
}
