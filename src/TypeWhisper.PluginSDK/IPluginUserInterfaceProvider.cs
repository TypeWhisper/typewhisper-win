namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional, UI-framework-independent commands contributed by a plugin.
/// The host does not read or display these commands. This contract is kept for
/// compatibility and will be removed in a future major SDK version.
/// </summary>
[Obsolete("The host does not display plugin commands. IPluginUserInterfaceProvider will be removed in a future major SDK version.")]
public interface IPluginUserInterfaceProvider : ITypeWhisperPlugin
{
    /// <summary>Commands intended for the tray. The host does not display them.</summary>
    IReadOnlyList<PluginCommandDescriptor> TrayCommands => [];

    /// <summary>Commands intended for a command launcher. The host does not display them.</summary>
    IReadOnlyList<PluginCommandDescriptor> QuickLaunchCommands => [];

    /// <summary>Executes a plugin-scoped command. The host does not invoke it.</summary>
    Task PerformCommandAsync(string commandId, CancellationToken cancellationToken);
}

/// <summary>
/// A localized plugin command descriptor. Contains no native UI objects.
/// The host does not display these commands; this type will be removed in a future major SDK version.
/// </summary>
[Obsolete("The host does not display plugin commands. PluginCommandDescriptor will be removed in a future major SDK version.")]
public sealed record PluginCommandDescriptor
{
    /// <summary>Stable identifier, unique within the contributing plugin.</summary>
    public required string Id { get; init; }

    /// <summary>Display title already localized by the plugin.</summary>
    public required string Title { get; init; }

    /// <summary>Optional semantic icon name.</summary>
    public string? IconName { get; init; }

    /// <summary>Whether the command can currently be invoked.</summary>
    public bool IsEnabled { get; init; } = true;
}
