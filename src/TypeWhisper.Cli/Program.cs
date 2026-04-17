using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace TypeWhisper.Cli;

/// <summary>
/// TypeWhisper CLI - communicates with the running TypeWhisper app via its REST API.
/// Usage: typewhisper [command] [options]
/// Commands: status, models, transcribe <file>
/// </summary>
static class Program
{
    private const int DefaultPort = 9876;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        if (args[0] == "--version")
        {
            Console.WriteLine($"typewhisper-cli {GetVersion()}");
            return 0;
        }

        var port = GetPort(args);
        var json = HasFlag(args, "--json");
        var baseUrl = $"http://127.0.0.1:{port}";

        return args[0] switch
        {
            "status" => await StatusAsync(baseUrl, json),
            "models" => await ModelsAsync(baseUrl, json),
            "transcribe" => await TranscribeAsync(baseUrl, args, json),
            _ => Error($"Unknown command: {args[0]}")
        };
    }

    static async Task<int> StatusAsync(string baseUrl, bool json)
    {
        try
        {
            var response = await Http.GetStringAsync($"{baseUrl}/v1/status");
            if (json) { Console.WriteLine(response); return 0; }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            Console.WriteLine($"Status: {Prop(root, "status")}");
            Console.WriteLine($"Model:  {Prop(root, "model")}");
            Console.WriteLine($"Engine: {Prop(root, "engine")}");
            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static async Task<int> ModelsAsync(string baseUrl, bool json)
    {
        try
        {
            var response = await Http.GetStringAsync($"{baseUrl}/v1/models");
            if (json) { Console.WriteLine(response); return 0; }

            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    var selected = m.TryGetProperty("selected", out var sel) && sel.GetBoolean() ? " *" : "";
                    Console.WriteLine($"  {Prop(m, "id"),-40} {Prop(m, "name")}{selected}");
                }
            }
            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static async Task<int> TranscribeAsync(string baseUrl, string[] args, bool json)
    {
        var file = args.Length > 1 ? args[1] : null;
        if (file is null || !File.Exists(file))
            return Error(file is null ? "Usage: typewhisper transcribe <file>" : $"File not found: {file}");

        var language = GetOption(args, "--language");
        var task = GetOption(args, "--task") ?? "transcribe";

        try
        {
            using var content = new MultipartFormDataContent();
            var fileBytes = await File.ReadAllBytesAsync(file);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", Path.GetFileName(file));
            content.Add(new StringContent(task), "task");
            if (language is not null) content.Add(new StringContent(language), "language");

            var response = await Http.PostAsync($"{baseUrl}/v1/transcribe", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return Error($"Transcription failed ({(int)response.StatusCode}): {body}");

            if (json) { Console.WriteLine(body); return 0; }

            using var doc = JsonDocument.Parse(body);
            Console.WriteLine(Prop(doc.RootElement, "text"));
            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
            TypeWhisper CLI - Speech-to-Text from the command line

            Usage: typewhisper <command> [options]

            Commands:
              status                    Show TypeWhisper status
              models                    List available models
              transcribe <file>         Transcribe an audio file

            Options:
              --port <N>                API server port (default: 9876)
              --json                    Output as JSON
              --language <code>         Source language (e.g. "en", "de")
              --task <task>             "transcribe" or "translate"
              --version                 Show version
              --help                    Show this help
            """);
    }

    static int GetPort(string[] args)
    {
        var idx = Array.IndexOf(args, "--port");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var port))
            return port;
        return DefaultPort;
    }

    static string? GetOption(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static bool HasFlag(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;

    static string GetVersion()
    {
        var info = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return Assembly.GetEntryAssembly()
            ?.GetName()
            .Version?
            .ToString() ?? "dev";
    }

    static string Prop(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    static int Error(string message) { Console.Error.WriteLine($"Error: {message}"); return 1; }
}
