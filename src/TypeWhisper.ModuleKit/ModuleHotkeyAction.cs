namespace TypeWhisper.ModuleKit;

/// <summary>An action the user can assign a global shortcut to in the Shortcuts settings.</summary>
/// <remarks>
/// Each action has one shortcut setting, saved under its identifier and empty at first. A shortcut that another global
/// shortcut already uses is refused.
/// </remarks>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="Title">The action's name in the settings and in conflict messages.</param>
/// <param name="Perform">Runs when the user presses the shortcut.</param>
/// <param name="Subtitle">A short explanation under the title, or null.</param>
/// <param name="Group">The heading the action is listed under, or null for a general heading.</param>
public sealed record ModuleHotkeyAction(string Id, string Title, Action Perform, string? Subtitle = null, string? Group = null);
