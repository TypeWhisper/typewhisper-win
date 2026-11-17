namespace TypeWhisper.ModuleKit;

/// <summary>
/// What modules add to the host. Each module fills its own instance in <see cref="IHostModule.Contribute"/>; the host
/// combines them once every module has contributed.
/// </summary>
/// <remarks>
/// Identifiers are stable, because the host stores settings under them: ASCII letters, digits, dots, hyphens and
/// underscores, at most 128 characters. Within one kind of contribution they are compared ignoring case. A
/// contribution with an invalid identifier, or with one that an earlier contribution of the same kind already uses, is
/// left out and reported. The host calls every delegate on the UI thread; one that throws is reported and does not
/// stop the app.
/// </remarks>
public sealed class ModuleContributions
{
    /// <summary>The managed policy keys that modules read through <see cref="IManagedPolicyReading"/>.</summary>
    public IList<ModuleManagedPolicyKey> ManagedPolicyKeys { get; } = new List<ModuleManagedPolicyKey>();

    /// <summary>Actions the user can assign a global shortcut to in the Shortcuts settings.</summary>
    public IList<ModuleHotkeyAction> HotkeyActions { get; } = new List<ModuleHotkeyAction>();

    /// <summary>Entries in the tray menu, after History.</summary>
    public IList<ModuleMenuItem> MenuItems { get; } = new List<ModuleMenuItem>();

    /// <summary>Floating panels, shown through <see cref="IModuleHost.ShowPanel"/> and the related members.</summary>
    public IList<ModulePanel> Panels { get; } = new List<ModulePanel>();

    /// <summary>Actions on History records.</summary>
    public IList<ModuleRecordAction> RecordActions { get; } = new List<ModuleRecordAction>();

    /// <summary>Cards in the Premium overview.</summary>
    public IList<ModulePremiumCard> PremiumCards { get; } = new List<ModulePremiumCard>();

    /// <summary>Sections appended to a settings page.</summary>
    public IList<ModuleSettingsSection> SettingsSections { get; } = new List<ModuleSettingsSection>();

    /// <summary>Content shown with the options of file transcription.</summary>
    public IList<ModuleFileTranscriptionOption> FileTranscriptionOptions { get; } = new List<ModuleFileTranscriptionOption>();

    /// <summary>Handlers called after a file transcription result was accepted.</summary>
    public IList<ModuleFileTranscriptionHook> FileTranscriptionCompleted { get; } = new List<ModuleFileTranscriptionHook>();

    /// <summary>Groups in the sidebar of the History window. Since ModuleKit 1.1.</summary>
    public IList<ModuleHistorySidebarGroup> HistorySidebarGroups { get; } = new List<ModuleHistorySidebarGroup>();
}
