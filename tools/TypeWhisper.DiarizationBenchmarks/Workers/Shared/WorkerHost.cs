using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypeWhisper.DiarizationBenchmarks.Workers;

/// <summary>
/// The JSON-lines protocol every benchmark worker speaks (the Python workers implement the same):
/// <c>{"op":"load","config":{…}}</c>, <c>{"op":"diarize","path":"…","probs":"…"}</c>, <c>{"op":"quit"}</c>.
/// One response line per request on stdout; logs go to stderr.
/// </summary>
internal static class WorkerHost
{
    public static int Run(Func<JsonObject, JsonObject> load, Func<string, string?, JsonObject> diarize)
    {
        var output = Console.Out;
        Console.SetOut(Console.Error);
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject response;
            var quit = false;
            try
            {
                var request = JsonNode.Parse(line)!.AsObject();
                var op = request["op"]?.GetValue<string>();
                switch (op)
                {
                    case "load":
                        response = load(request["config"]!.AsObject());
                        response["ok"] = true;
                        break;
                    case "diarize":
                        response = diarize(request["path"]!.GetValue<string>(), request["probs"]?.GetValue<string>());
                        break;
                    case "quit":
                        response = new JsonObject { ["ok"] = true };
                        quit = true;
                        break;
                    default:
                        response = new JsonObject { ["error"] = $"unknown op {op}" };
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                response = new JsonObject { ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
            }

            output.WriteLine(response.ToJsonString());
            output.Flush();
            if (quit)
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>Turns as the hypothesis file stores them: [start, end, label], sorted by start.</summary>
    public static JsonArray TurnsToJson(IEnumerable<(double Start, double End, string Speaker)> turns)
    {
        var array = new JsonArray();
        foreach (var (start, end, speaker) in turns.OrderBy(t => t.Start).ThenBy(t => t.End).ThenBy(t => t.Speaker, StringComparer.Ordinal))
        {
            array.Add(new JsonArray(Math.Round(start, 3), Math.Round(end, 3), speaker));
        }

        return array;
    }

    public static double ElapsedMs(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    public static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
