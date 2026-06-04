namespace TypeWhisper.Core.Models;

/// <summary>
/// Localized labels for history export methods.
/// Callers pass localized strings; defaults are English.
/// </summary>
public sealed record ExportLabels
{
    /// <summary>
    /// Gets or sets the header value.
    /// </summary>
    public string Header { get; init; } = "TypeWhisper — Transcription History";
    /// <summary>
    /// Gets or sets the exported value.
    /// </summary>
    public string Exported { get; init; } = "Exported";
    /// <summary>
    /// Gets or sets the entries value.
    /// </summary>
    public string Entries { get; init; } = "Entries";
    /// <summary>
    /// Gets or sets the timestamp value.
    /// </summary>
    public string Timestamp { get; init; } = "Timestamp";
    /// <summary>
    /// Gets or sets the app value.
    /// </summary>
    public string App { get; init; } = "App";
    /// <summary>
    /// Gets or sets the text value.
    /// </summary>
    public string Text { get; init; } = "Text";
    /// <summary>
    /// Returns the duration.
    /// </summary>
    public string Duration { get; init; } = "Duration (s)";
    /// <summary>
    /// Gets or sets the words value.
    /// </summary>
    public string Words { get; init; } = "Words";
    /// <summary>
    /// Gets or sets the language value.
    /// </summary>
    public string Language { get; init; } = "Language";

    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static ExportLabels Default { get; } = new();
}
