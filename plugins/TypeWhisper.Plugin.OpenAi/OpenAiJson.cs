using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.OpenAi;

internal static class OpenAiJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    public static JsonElement Element<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions).Clone();

    public static StringContent CreateJsonContent(IReadOnlyDictionary<string, JsonElement> body) =>
        new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
}
