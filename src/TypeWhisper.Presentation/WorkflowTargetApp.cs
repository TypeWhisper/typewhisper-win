using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>
/// The app a dictation workflow sends its text to instead of the window the dictation started in (#764), for example
/// a chat app on another screen while a game is in front.
/// </summary>
public static class WorkflowTargetApp
{
    /// <summary>The longest process name a workflow can name.</summary>
    public const int MaxLength = WorkflowTargetAppName.MaxLength;

    /// <summary>Whether a value is a process name such as discord, without a path or ".exe".</summary>
    public static bool IsValid(string? value) => WorkflowTargetAppName.IsValid(value);

    /// <summary>The trimmed process name a workflow sends its dictation to, or null when it inserts where the dictation
    /// started. A plugin action receives the text instead, so it never sends to an app.</summary>
    public static string? Of(Workflow workflow) =>
        string.IsNullOrWhiteSpace(workflow.Output.TargetActionPluginId) && IsValid(workflow.Output.TargetAppProcessName)
            ? workflow.Output.TargetAppProcessName!.Trim() : null;
}
