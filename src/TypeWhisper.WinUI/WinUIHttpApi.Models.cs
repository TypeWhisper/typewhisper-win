using System.Text.Json;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class WinUIHttpApi
{
    private async Task<LocalApiResponse?> HandleModelsAsync(LocalApiRequest request, CancellationToken ct)
    {
        if (request.Path is not ("/v1/models" or "/v1/models/load" or "/v1/models/unload")) return null;
        if (request.Path == "/v1/models" && request.Method == "GET")
            return LocalApiResponse.Json(200, session.ApiModelSnapshot());
        var operation = request.Path switch
        {
            "/v1/models/load" when request.Method == "POST" => "load",
            "/v1/models/unload" when request.Method == "POST" => "unload",
            "/v1/models" when request.Method == "DELETE" => "delete",
            _ => null
        };
        if (operation is null) return Error(405, "Method not allowed.");
        string? engine;
        string? model;
        if (operation == "delete")
        {
            engine = request.Query.GetValueOrDefault("engine")?.Trim();
            model = request.Query.GetValueOrDefault("model")?.Trim();
        }
        else
        {
            try
            {
                using var json = JsonDocument.Parse(request.Body);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(item => item.Name).Any(group => group.Count() > 1))
                    return Error(400, "Expected JSON with 'engine' and optional 'model'.");
                engine = root.GetProperty("engine").GetString()?.Trim();
                model = root.TryGetProperty("model", out var value) ? value.GetString()?.Trim() : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { return Error(400, "Expected JSON with 'engine' and optional 'model'."); }
        }
        if (string.IsNullOrWhiteSpace(engine) || operation != "unload" && string.IsNullOrWhiteSpace(model))
            return Error(400, operation == "unload" ? "'engine' is required." : "Both 'engine' and 'model' are required.");
        var provider = session.ApiModelProvider(engine);
        if (provider is null) return Error(404, "Engine not found.");
        if (operation != "unload" && !provider.Models.Any(item => item.Id == model)) return Error(404, "Model not found for this engine.");
        try
        {
            var resultModel = await session.ApiChangeModelAsync(operation, provider, model, ct);
            return LocalApiResponse.Json(200, new { engine, model = resultModel, status = operation switch { "load" => "ready", "unload" => "unloaded", _ => "deleted" } });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        { return Error(409, ex.Message); }
    }
}
