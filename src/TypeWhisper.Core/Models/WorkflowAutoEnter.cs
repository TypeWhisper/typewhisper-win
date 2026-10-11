using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Models;

/// <summary>
/// Lists when a workflow presses Enter after inserting its text, matching the macOS raw values.
/// </summary>
public enum WorkflowAutoEnterMode
{
    /// <summary>
    /// Only inserts the text.
    /// </summary>
    Never,
    /// <summary>
    /// Presses Enter when the dictation ends with "press enter" or "press return".
    /// </summary>
    SpokenCommand,
    /// <summary>
    /// Presses Enter after every inserted dictation.
    /// </summary>
    Always,
    /// <summary>
    /// Presses Enter when the user stopped the recording with a physical Enter.
    /// </summary>
    DuringDictation
}

/// <summary>
/// Provides conversion helpers for workflow auto enter modes.
/// </summary>
public static class WorkflowAutoEnterModes
{
    /// <summary>
    /// Parses a raw mode; a missing or unknown value falls back to the legacy flag, as on macOS.
    /// </summary>
    public static WorkflowAutoEnterMode Parse(string? rawValue, bool autoEnter) => TryParse(rawValue, out var mode)
        ? mode : autoEnter ? WorkflowAutoEnterMode.Always : WorkflowAutoEnterMode.Never;

    /// <summary>
    /// Returns whether a raw value is absent or one of the known modes.
    /// </summary>
    public static bool IsKnown(string? rawValue) => rawValue is null || TryParse(rawValue, out _);

    /// <summary>
    /// Converts a mode to its serialized value.
    /// </summary>
    public static string ToRawValue(this WorkflowAutoEnterMode mode) => mode switch
    {
        WorkflowAutoEnterMode.SpokenCommand => "spokenCommand",
        WorkflowAutoEnterMode.Always => "always",
        WorkflowAutoEnterMode.DuringDictation => "duringDictation",
        _ => "never"
    };

    private static bool TryParse(string? rawValue, out WorkflowAutoEnterMode mode)
    {
        mode = rawValue switch
        {
            "never" => WorkflowAutoEnterMode.Never,
            "spokenCommand" => WorkflowAutoEnterMode.SpokenCommand,
            "always" => WorkflowAutoEnterMode.Always,
            "duringDictation" => WorkflowAutoEnterMode.DuringDictation,
            _ => (WorkflowAutoEnterMode)(-1)
        };
        return Enum.IsDefined(mode);
    }
}

/// <summary>
/// The text to insert and whether Enter follows it.
/// </summary>
public readonly record struct WorkflowAutoEnterResolution(string Text, bool ShouldPressEnter);

/// <summary>
/// Decides whether a dictation is submitted, matching the macOS resolver.
/// </summary>
public static partial class WorkflowAutoEnterResolver
{
    /// <summary>
    /// Resolves the text before post-processing. A spoken command is removed from the text it submits.
    /// </summary>
    public static WorkflowAutoEnterResolution Resolve(string text, WorkflowAutoEnterMode mode, bool submitRequested = false)
    {
        var trimmed = text.Trim();
        switch (mode)
        {
            case WorkflowAutoEnterMode.Always: return new(text, true);
            case WorkflowAutoEnterMode.DuringDictation: return new(text, submitRequested && trimmed.Length > 0);
            case WorkflowAutoEnterMode.SpokenCommand: break;
            default: return new(text, false);
        }
        var match = SpokenCommand().Match(trimmed);
        var content = match.Success ? match.Groups[1].Value.Trim() : "";
        return content.Length == 0 ? new(text, false) : new(content, true);
    }

    /// <summary>
    /// Decides whether Enter follows the insertion. Always does not submit a dictation whose processing
    /// failed; an explicit request by voice or key still submits non-empty text.
    /// </summary>
    public static bool ShouldPressEnterAfterInsertion(WorkflowAutoEnterMode mode, WorkflowAutoEnterResolution resolution,
        string insertedText, bool processingFailed)
    {
        if (mode == WorkflowAutoEnterMode.Always) return !processingFailed;
        return resolution.ShouldPressEnter && !string.IsNullOrWhiteSpace(insertedText);
    }

    [GeneratedRegex(@"^(.+?)\s+press\s+(?:enter|return)[\s\p{P}\p{S}]*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SpokenCommand();
}
