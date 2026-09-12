namespace TypeWhisper.PluginSDK;

/// <summary>A bounded text setting rendered by the host without a framework-specific plugin view.</summary>
public sealed record PluginTextSetting(string Id, string Title, string Description, string Value, int MaxLength = 32768)
{
    /// <summary>Logical section used by the host to place related settings together.</summary>
    public PluginSettingsSection Section { get; init; } = PluginSettingsSection.General;
    /// <summary>Choice changes are committed immediately; text fields always require explicit saving.</summary>
    public bool SaveChoiceOnChange { get; init; }
    /// <summary>Gets whether the host should allow multiple lines. Defaults to a single-line field.</summary>
    public bool IsMultiline { get; init; }
    /// <summary>Optional allowed values rendered as a selection instead of free text.</summary>
    public IReadOnlyList<PluginSettingChoice> Choices { get; init; } = [];
    /// <summary>Optional suggestions for free text. Values outside this list remain valid.</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];
    /// <summary>Optional display condition evaluated against current editor values, including unsaved edits.</summary>
    public PluginSettingCondition? VisibleWhen { get; init; }
}

/// <summary>A setting is shown when another setting has one of the given values.</summary>
public sealed record PluginSettingCondition(string SettingId, IReadOnlyList<string> Values);

/// <summary>A stable setting value with a localized display title.</summary>
public sealed record PluginSettingChoice(string Value, string Title);

/// <summary>Provides persistent text settings. Hosts call this capability within the package configuration lease.</summary>
public interface IPluginTextSettings
{
    /// <summary>Returns current values; the host must preserve unsaved edits while capabilities refresh.</summary>
    IReadOnlyList<PluginTextSetting> TextSettings { get; }
    /// <summary>Persists a value before publishing it. Failed writes must retain the preceding value.</summary>
    Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken);
}

/// <summary>Host-owned settings sections in display order.</summary>
public enum PluginSettingsSection
{
    /// <summary>Provider authentication, before models and other settings.</summary>
    Connection,
    /// <summary>Audio transcription preferences.</summary>
    Transcription,
    /// <summary>Speech synthesis preferences.</summary>
    Speech,
    /// <summary>Text processing preferences.</summary>
    TextProcessing,
    /// <summary>Unsectioned settings for existing plugins.</summary>
    General
}

/// <summary>Optional connection UI state. Hiding key entry never removes the stored key.</summary>
public interface IPluginConnectionSettings
{
    /// <summary>Opaque identity of the configured connection. Hosts discard unsaved key input when it changes.</summary>
    string? ConnectionIdentity => null;

    /// <summary>Whether the currently selected connection method exposes host API-key entry.</summary>
    bool ShowApiKeySettings { get; }
}
