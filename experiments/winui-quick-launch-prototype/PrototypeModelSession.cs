namespace TypeWhisper.WinUIPrototype;

internal sealed record PrototypeModelOption(string Id, string Title, string Description, string Badge);

// Fictional catalog and in-memory download simulation. No model files or services.
internal sealed class PrototypeModelSession(Dictionary<string, string> values)
{
    internal static readonly PrototypeModelOption[] Models =
    [
        new("demo-light", "Light", "A small-model example for quick, everyday dictation.", "LIGHTWEIGHT"),
        new("demo-balanced", "Balanced", "The all-round option in this sample catalog.", "EVERYDAY"),
        new("demo-full", "Full", "A larger-model example for longer recordings.", "EXTENDED")
    ];
    private const string InstalledKey = "PrototypeModels.Downloaded";
    internal string? Downloading { get; private set; }
    internal int Progress { get; private set; }
    internal string Active => values.GetValueOrDefault("SelectedModelId", "Not selected");
    internal bool IsDownloaded(string id) => values.GetValueOrDefault(InstalledKey, "demo-balanced").Split(',').Contains(id);
    private static bool Known(string id) => Models.Any(model => model.Id == id);

    internal bool StartDownload(string id)
    {
        if (!Known(id) || Downloading is not null || IsDownloaded(id)) return false;
        Downloading = id; Progress = 0; return true;
    }

    internal bool Advance()
    {
        if (Downloading is null) return false;
        Progress = Math.Min(100, Progress + 10);
        if (Progress < 100) return false;
        var installed = values.GetValueOrDefault(InstalledKey, "demo-balanced")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Append(Downloading).Distinct();
        values[InstalledKey] = string.Join(',', installed);
        Downloading = null;
        return true;
    }

    internal void CancelDownload() { Downloading = null; Progress = 0; }

    internal bool Activate(string id)
    {
        if (!Known(id) || !IsDownloaded(id)) return false;
        values["SelectedModelId"] = id;
        return true;
    }
}
