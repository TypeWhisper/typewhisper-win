using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

/// <summary>
/// Provides cloud folder sync json behavior.
/// </summary>
public static class CloudFolderSyncJson
{
    /// <summary>
    /// Creates options.
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions(writeIndented: true);

    /// <summary>
    /// Serializes&lt;t&gt;.
    /// </summary>
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserializes&lt;t&gt;.
    /// </summary>
    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    internal static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new SyncDictionaryEntryJsonConverter());
        options.Converters.Add(new DictionaryEntrySourceJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new CloudFolderSyncDateTimeConverter());
        return options;
    }
}

internal sealed class CloudFolderSyncDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Reads.
    /// </summary>
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("Expected an ISO-8601 date string.");

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.UtcDateTime;
        }

        throw new JsonException($"Invalid ISO-8601 date: {value}");
    }

    /// <summary>
    /// Writes.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString(Format, CultureInfo.InvariantCulture));
    }
}

internal sealed class SyncDictionaryEntryJsonConverter : JsonConverter<UserDataSyncDictionaryEntry>
{
    private JsonSerializerOptions WithoutThisConverter(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);
        copy.Converters.Remove(this);
        return copy;
    }

    public override UserDataSyncDictionaryEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var entry = document.RootElement.Deserialize<UserDataSyncDictionaryEntry>(WithoutThisConverter(options))
            ?? throw new JsonException("Missing dictionary entry.");
        return entry with { IsRegexFieldPresent = document.RootElement.TryGetProperty("isRegex", out _) };
    }

    public override void Write(Utf8JsonWriter writer, UserDataSyncDictionaryEntry value, JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(value, WithoutThisConverter(options));
        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("isRegex") && !value.IsRegexFieldPresent) continue;
            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
