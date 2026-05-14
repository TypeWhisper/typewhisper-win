using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// A plugin entry from the remote plugin registry.
/// </summary>
public sealed record RegistryPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string? MinHostVersion { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Category { get; init; }
    [JsonConverter(typeof(RegistryCategoriesJsonConverter))]
    public IReadOnlyList<string>? Categories { get; init; }
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string? IconSystemName { get; init; }
    public bool RequiresApiKey { get; init; }
    public Dictionary<string, string>? Descriptions { get; init; }
}

internal sealed class RegistryCategoriesJsonConverter : JsonConverter<IReadOnlyList<string>?>
{
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
            throw new JsonException("Registry plugin categories must be a string or an array of strings.");

        var categories = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return categories;

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Registry plugin categories must only contain strings.");

            categories.Add(reader.GetString() ?? string.Empty);
        }

        throw new JsonException("Registry plugin categories array was not closed.");
    }

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
/// Installation state of a registry plugin relative to the local system.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginInstallState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Bundled
}
