namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional, UI-framework-independent commands contributed by a plugin.
/// The host owns menu rendering and scopes command identifiers by plugin ID.
/// Notify the host through NotifyCapabilitiesChanged when descriptors change.
/// </summary>
public interface IPluginUserInterfaceProvider : ITypeWhisperPlugin
{
    /// <summary>Commands shown under the plugin's integration submenu in the tray.</summary>
    IReadOnlyList<PluginCommandDescriptor> TrayCommands => [];

    /// <summary>Commands searchable in Quick Launch, independently of tray visibility.</summary>
    IReadOnlyList<PluginCommandDescriptor> QuickLaunchCommands => [];

    /// <summary>Executes a plugin-scoped command; the host handles errors and cancellation.</summary>
    Task PerformCommandAsync(string commandId, CancellationToken cancellationToken);
}

/// <summary>A localized, host-rendered plugin command. Contains no native UI objects.</summary>
public sealed record PluginCommandDescriptor
{
    /// <summary>Stable identifier, unique within the contributing plugin.</summary>
    public required string Id { get; init; }

    /// <summary>Display title already localized by the plugin.</summary>
    public required string Title { get; init; }

    /// <summary>Optional semantic host icon name; unknown names use a generic plugin icon.</summary>
    public string? IconName { get; init; }

    /// <summary>Whether the command can currently be invoked.</summary>
    public bool IsEnabled { get; init; } = true;
}
