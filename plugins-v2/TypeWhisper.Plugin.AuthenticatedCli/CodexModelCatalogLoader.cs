using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal static class CodexModelCatalogLoader
{
    internal static Task<IReadOnlyList<PluginModelInfo>> LoadAsync(string executable, string directory, CancellationToken ct) =>
        CliProcessRunner.RunProtocolAsync(new CliProcessRequest(executable,
            ["app-server", "--stdio", "-c", "analytics.enabled=false", "-c", "check_for_update_on_startup=false"],
            "", directory, ["CODEX_HOME"], TimeSpan.FromSeconds(20), 2 * 1024 * 1024, 64 * 1024), ExchangeAsync, ct);

    internal static async Task<IReadOnlyList<PluginModelInfo>> ExchangeAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var writer = new StreamWriter(input, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(output, new UTF8Encoding(false, true), leaveOpen: true);
        var remainingCharacters = 2 * 1024 * 1024;
        async Task Send(object message) => await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        async Task<JsonElement> Receive(int id)
        {
            while (true)
            {
                var line = new StringBuilder();
                var character = new char[1];
                while (true)
                {
                    if (--remainingCharacters < 0) throw new IOException("Codex model response is too large.");
                    if (await reader.ReadAsync(character.AsMemory(), ct) == 0) throw new IOException("Codex model connection closed.");
                    if (character[0] == '\n') break;
                    line.Append(character[0]);
                }
                using var document = JsonDocument.Parse(line.ToString());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var responseId)
                    || !responseId.TryGetInt32(out var value) || value != id) continue;
                if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result))
                    throw new IOException("Codex model discovery failed.");
                return result.Clone();
            }
        }
        await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "typewhisper", version = "1.2.1" } } });
        await Receive(1);
        await Send(new { method = "initialized" });
        var models = new List<PluginModelInfo>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            await Send(new { id = page + 2, method = "model/list", @params = new { limit = 100, includeHidden = false, cursor } });
            var result = await Receive(page + 2);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new IOException("Codex returned an invalid model catalog.");
            foreach (var model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("model", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var key = id.GetString()!;
                if (!IsModelId(key) || (model.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)) continue;
                var name = model.TryGetProperty("displayName", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() : null;
                models.Add(new(key, string.IsNullOrWhiteSpace(name) ? key : name) { IsRecommended = model.TryGetProperty("isDefault", out var recommended) && recommended.ValueKind == JsonValueKind.True });
            }
            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) return models.DistinctBy(m => m.Id, StringComparer.Ordinal).ToArray();
            if (!cursors.Add(cursor)) throw new IOException("Codex repeated a model catalog page.");
        }
        throw new IOException("Codex model catalog exceeded the page limit.");
    }

    internal static bool IsModelId(string value) => value.Length is > 0 and <= 256
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':');
}
