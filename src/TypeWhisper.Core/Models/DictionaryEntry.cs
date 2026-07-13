using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents dictionary entry data.
/// </summary>
public sealed record DictionaryEntry
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the entry type value.
    /// </summary>
    public required DictionaryEntryType EntryType { get; init; }
    /// <summary>
    /// Gets or sets the original value.
    /// </summary>
    public required string Original { get; init; }
    /// <summary>
    /// Gets or sets the replacement value.
    /// </summary>
    public string? Replacement { get; init; }
    /// <summary>
    /// Gets or sets the case sensitive value.
    /// </summary>
    public bool CaseSensitive { get; init; }
    /// <summary>
    /// Gets or sets the is enabled value.
    /// </summary>
    public bool IsEnabled { get; init; } = true;
    /// <summary>
    /// Gets the origin of this dictionary entry.
    /// </summary>
    public DictionaryEntrySource Source { get; init; } = DictionaryEntrySource.Manual;
    /// <summary>
    /// Gets or sets the usage count value.
    /// </summary>
    public int UsageCount { get; init; }
    /// <summary>
    /// Gets or sets the created at value.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Gets or sets the updated at value.
    /// </summary>
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// Lists the supported dictionary entry type values.
/// </summary>
public enum DictionaryEntryType
{
    /// <summary>
    /// Represents the term option.
    /// </summary>
    Term,
    /// <summary>
    /// Represents the correction option.
    /// </summary>
    Correction
}

/// <summary>
/// Lists the supported dictionary entry source values.
/// </summary>
[JsonConverter(typeof(DictionaryEntrySourceJsonConverter))]
public enum DictionaryEntrySource
{
    /// <summary>
    /// Represents an entry created explicitly by the user or an external client.
    /// </summary>
    Manual,
    /// <summary>
    /// Represents a correction learned automatically from a target-app edit.
    /// </summary>
    AutoLearned
}

/// <summary>
/// Reads correction provenance conservatively so legacy and future values remain manual.
/// </summary>
internal sealed class DictionaryEntrySourceJsonConverter : JsonConverter<DictionaryEntrySource>
{
    /// <inheritdoc />
    public override DictionaryEntrySource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return string.Equals(reader.GetString(), "autoLearned", StringComparison.OrdinalIgnoreCase)
                ? DictionaryEntrySource.AutoLearned
                : DictionaryEntrySource.Manual;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue))
            return numericValue == (int)DictionaryEntrySource.AutoLearned
                ? DictionaryEntrySource.AutoLearned
                : DictionaryEntrySource.Manual;

        reader.Skip();
        return DictionaryEntrySource.Manual;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DictionaryEntrySource value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == DictionaryEntrySource.AutoLearned ? "autoLearned" : "manual");
}
