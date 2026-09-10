namespace TypeWhisper.PluginSDK;

/// <summary>A bounded text setting rendered by the host without a framework-specific plugin view.</summary>
public sealed record PluginTextSetting(string Id, string Title, string Description, string Value, int MaxLength = 32768)
{
    /// <summary>Gets whether the host should allow multiple lines. Defaults to a single-line field.</summary>
    public bool IsMultiline { get; init; }
    /// <summary>Optional allowed values rendered as a selection instead of free text.</summary>
    public IReadOnlyList<PluginSettingChoice> Choices { get; init; } = [];
}

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
