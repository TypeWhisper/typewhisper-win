using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// A plugin entry from the remote plugin registry.
/// </summary>
public sealed record RegistryPlugin
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public string Id { get; init; } = "";
    /// <summary>
    /// Gets or sets the name value.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// Gets or sets the version value.
    /// </summary>
    public string Version { get; init; } = "0.0.0";
    /// <summary>
    /// Gets or sets the min host version value.
    /// </summary>
    public string? MinHostVersion { get; init; }
    /// <summary>
    /// Gets or sets the author value.
    /// </summary>
    public string Author { get; init; } = "";
    /// <summary>
    /// Gets or sets the description value.
    /// </summary>
    public string Description { get; init; } = "";
    /// <summary>
    /// Gets or sets the category value.
    /// </summary>
    public string? Category { get; init; }
    /// <summary>
    /// Gets or sets the categories value.
    /// </summary>
    [JsonConverter(typeof(RegistryStringListJsonConverter))]
    public IReadOnlyList<string>? Categories { get; init; }
    /// <summary>
    /// Gets or sets the supported host platforms. A missing value preserves legacy Windows compatibility.
    /// </summary>
    [JsonConverter(typeof(RegistryStringListJsonConverter))]
    public IReadOnlyList<string>? Platforms { get; init; }
    /// <summary>
    /// Gets or sets the supported Windows process architectures.
    /// </summary>
    [JsonConverter(typeof(RegistryStringListJsonConverter))]
    public IReadOnlyList<string>? SupportedArchitectures { get; init; }
    /// <summary>
    /// Gets or sets the size value.
    /// </summary>
    public long Size { get; init; }
    /// <summary>
    /// Gets or sets the download url value.
    /// </summary>
    public string DownloadUrl { get; init; } = "";
    /// <summary>
    /// Gets or sets the expected SHA-256 hash for the plugin package.
    /// </summary>
    public string? Sha256 { get; init; }
    /// <summary>
    /// Gets or sets the publisher source classification covered by the artifact attestation.
    /// </summary>
    public string? Source { get; init; }
    /// <summary>
    /// Gets or sets the build trust classification covered by the artifact attestation.
    /// </summary>
    public string? Trust { get; init; }
    /// <summary>
    /// Gets or sets the canonical source repository covered by the artifact attestation.
    /// </summary>
    public string? SourceRepository { get; init; }
    /// <summary>
    /// Gets or sets the cryptographic build attestation.
    /// </summary>
    public RegistryArtifactAttestation? Attestation { get; init; }
    /// <summary>
    /// Gets or sets the icon system name value.
    /// </summary>
    public string? IconSystemName { get; init; }
    /// <summary>
    /// Gets or sets the requires api key value.
    /// </summary>
    public bool RequiresApiKey { get; init; }
    /// <summary>
    /// Gets or sets the descriptions value.
    /// </summary>
    public Dictionary<string, string>? Descriptions { get; init; }
    /// <summary>
    /// Gets or sets the hosting classification. Unknown future values remain parseable.
    /// </summary>
    public string? Hosting { get; init; }
    /// <summary>
    /// Gets whether the plugin is classified as cloud-hosted.
    /// </summary>
    [JsonIgnore]
    public bool IsCloudHosted => Hosting?.Trim().ToLowerInvariant() switch
    {
        "cloud" => true,
        "local" => false,
        _ => RequiresApiKey
    };
}

internal sealed class RegistryStringListJsonConverter : JsonConverter<IReadOnlyList<string>?>
{
    /// <summary>
    /// Reads.
    /// </summary>
    public override IReadOnlyList<string>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString() ?? string.Empty];

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Registry string lists must be a string or an array of strings.");

        var categories = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return categories;

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Registry string lists must only contain strings.");

            categories.Add(reader.GetString() ?? string.Empty);
        }

        throw new JsonException("Registry string list array was not closed.");
    }

    /// <summary>
    /// Writes.
    /// </summary>
    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<string>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var category in value)
            writer.WriteStringValue(category);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Lists the supported plugin install state values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginInstallState
{
    /// <summary>
    /// Represents the not installed option.
    /// </summary>
    NotInstalled,
    /// <summary>
    /// Represents the installed option.
    /// </summary>
    Installed,
    /// <summary>
    /// Represents the update available option.
    /// </summary>
    UpdateAvailable,
    /// <summary>
    /// Represents the pending restart option.
    /// </summary>
    PendingRestart,
    /// <summary>
    /// Represents the bundled option.
    /// </summary>
    Bundled,
    /// <summary>
    /// Represents an installed plugin that needs repair.
    /// </summary>
    Broken
}

/// <summary>
/// Describes the primary reason why an installed plugin needs attention.
/// </summary>
public enum PluginDiagnosticCode
{
    /// <summary>
    /// No diagnostic issue was detected.
    /// </summary>
    None,
    /// <summary>
    /// One or more required plugin files are missing.
    /// </summary>
    MissingFiles,
    /// <summary>
    /// The plugin manifest cannot be read or does not match the expected identity.
    /// </summary>
    InvalidManifest,
    /// <summary>
    /// Installed files no longer match the recorded registry package.
    /// </summary>
    IntegrityMismatch,
    /// <summary>
    /// The plugin could not be loaded or activated.
    /// </summary>
    LoadFailure,
    /// <summary>
    /// The plugin directory cannot be inspected because access was denied.
    /// </summary>
    PermissionDenied,
    /// <summary>
    /// A previous install or update did not finish cleanly.
    /// </summary>
    InterruptedInstallation
}

/// <summary>
/// Provides the deterministic install diagnosis for a registry plugin.
/// </summary>
/// <param name="State">The effective install state.</param>
/// <param name="DiagnosticCode">The primary diagnostic code.</param>
/// <param name="IsRegistryManaged">Whether a structurally valid registry install receipt exists.</param>
/// <param name="Details">A support-oriented diagnostic detail without user data.</param>
public sealed record PluginInstallDiagnosis(
    PluginInstallState State,
    PluginDiagnosticCode DiagnosticCode,
    bool IsRegistryManaged,
    string? Details = null);

/// <summary>
/// Lists the supported plugin install result values.
/// </summary>
public enum PluginInstallResult
{
    /// <summary>
    /// Represents a plugin installed and loaded in the current process.
    /// </summary>
    Installed,
    /// <summary>
    /// Represents a plugin package staged for the next app restart.
    /// </summary>
    PendingRestart
}

/// <summary>
/// Lists the supported plugin uninstall result values.
/// </summary>
public enum PluginUninstallResult
{
    /// <summary>
    /// Represents a plugin removed from disk in the current process.
    /// </summary>
    Uninstalled,
    /// <summary>
    /// Represents a plugin removal queued for the next app restart.
    /// </summary>
    PendingRestart
}
