using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Cli;

/// <summary>
/// TypeWhisper CLI - communicates with the running TypeWhisper app via its REST API.
/// </summary>
static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"typewhisper-cli {GetVersion()}");
            return 0;
        }

        if (options.Error is not null)
            return Error(options.Error);

        if (options.Command is null)
        {
            PrintUsage();
            return 1;
        }

        var connection = CliConnectionResolver.Resolve(new CliConnectionOptions(
            PortOverride: options.Port,
            ApiTokenOverride: options.ApiToken,
            EnvironmentApiToken: Environment.GetEnvironmentVariable("TYPEWHISPER_API_TOKEN")));
        var baseUrl = $"http://127.0.0.1:{connection.Port}";

        return options.Command switch
        {
            "status" => await StatusAsync(baseUrl, options.Json, connection.ApiToken),
            "models" => await ModelsAsync(baseUrl, options.Json, connection.ApiToken),
            "transcribe" => await TranscribeAsync(baseUrl, options, connection.ApiToken),
            "export" => await ExportSettingsAsync(baseUrl, options, connection.ApiToken),
            "import" => await ImportSettingsAsync(baseUrl, options, connection.ApiToken),
            _ => Error($"Unknown command: {options.Command}")
        };
    }

    static async Task<int> ExportSettingsAsync(string baseUrl, CliOptions options, string? apiToken)
    {
        if (options.Positionals.Count != 1 || string.IsNullOrWhiteSpace(options.Positionals[0]))
            return Error("Usage: typewhisper export <path>");

        var path = Path.GetFullPath(options.Positionals[0]);
        try
        {
            using var request = CliRequestBuilder.BuildGet(baseUrl, "/v1/settings/export", apiToken);
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error($"Backup export failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");

            if (Encoding.UTF8.GetByteCount(body) > CliBackupFile.MaxBackupBytes)
                return Error($"Exported backup exceeds the {CliBackupFile.MaxBackupBytes / (1024 * 1024)} MiB size limit.");

            using (JsonDocument.Parse(body)) { }
            await CliBackupFile.WriteAtomicAsync(path, body);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    path,
                    bytes = Encoding.UTF8.GetByteCount(body)
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine($"Backup exported to {path}");
            }

            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Error($"Could not export backup: {ex.Message}");
        }
    }

    static async Task<int> ImportSettingsAsync(string baseUrl, CliOptions options, string? apiToken)
    {
        if (options.Positionals.Count != 1 || string.IsNullOrWhiteSpace(options.Positionals[0]))
            return Error("Usage: typewhisper import <path> [--json]");

        var path = Path.GetFullPath(options.Positionals[0]);
        if (!File.Exists(path))
            return Error($"File not found: {path}");

        try
        {
            var backupJson = await CliBackupFile.ReadAsync(path);
            using (JsonDocument.Parse(backupJson)) { }

            using var request = CliRequestBuilder.BuildSettingsImport(baseUrl, backupJson, apiToken);
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error($"Backup import failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");

            if (options.Json)
            {
                Console.WriteLine(PrettyJson(body));
                return ImportSucceeded(body) ? 0 : 1;
            }

            return PrintImportSummary(body);
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return Error($"Could not import backup: {ex.Message}");
        }
    }

    static bool ImportSucceeded(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return !doc.RootElement.TryGetProperty("success", out var success) || success.GetBoolean();
    }

    static int PrintImportSummary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var success = !root.TryGetProperty("success", out var successElement) || successElement.GetBoolean();
        Console.WriteLine(success ? "Backup restored." : "Backup restore failed.");

        if (root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Object)
        {
            foreach (var category in categories.EnumerateObject())
            {
                var result = category.Value;
                Console.WriteLine(
                    $"  {category.Name}: {Count(result, "imported")} imported, " +
                    $"{Count(result, "skipped")} skipped, {Count(result, "conflicts")} conflicts");
            }
        }

        if (root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
        {
            foreach (var warning in warnings.EnumerateArray())
                Console.WriteLine($"Warning: {warning.GetString()}");
        }

        if (root.TryGetProperty("restart_required", out var restart) && restart.ValueKind == JsonValueKind.True)
            Console.WriteLine("Restart TypeWhisper to finish restoring plugins.");
        if (!success && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            Console.Error.WriteLine($"Error: {error.GetString()}");

        return success ? 0 : 1;
    }

    static int Count(JsonElement result, string name) =>
        result.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;

    static async Task<int> StatusAsync(string baseUrl, bool json, string? apiToken)
    {
        try
        {
            using var request = CliRequestBuilder.BuildGet(baseUrl, "/v1/status", apiToken);
            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error($"Status request failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");

            if (json) { Console.WriteLine(PrettyJson(body)); return 0; }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = Prop(root, "status") == "ready" ? "Ready" : "No model loaded";
            var engine = Prop(root, "engine");
            var model = Prop(root, "model");
            Console.WriteLine(string.IsNullOrEmpty(model)
                ? $"{status} - {engine}"
                : $"{status} - {engine} ({model})");
            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static async Task<int> ModelsAsync(string baseUrl, bool json, string? apiToken)
    {
        try
        {
            using var request = CliRequestBuilder.BuildGet(baseUrl, "/v1/models", apiToken);
            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error($"Models request failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");

            if (json) { Console.WriteLine(PrettyJson(body)); return 0; }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models))
                return 0;

            var rows = models.EnumerateArray().ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine("No models available.");
                return 0;
            }

            var idWidth = Math.Max(2, rows.Max(m => Prop(m, "id").Length));
            var engineWidth = Math.Max(6, rows.Max(m => Prop(m, "engine").Length));
            var nameWidth = Math.Max(4, rows.Max(m => Prop(m, "name").Length));

            Console.WriteLine($"{Pad("ID", idWidth)}  {Pad("ENGINE", engineWidth)}  {Pad("NAME", nameWidth)}  STATUS");
            Console.WriteLine(new string('-', idWidth + engineWidth + nameWidth + 10));

            foreach (var m in rows)
            {
                var selected = m.TryGetProperty("selected", out var sel) && sel.GetBoolean() ? " *" : "";
                Console.WriteLine(
                    $"{Pad(Prop(m, "id"), idWidth)}  {Pad(Prop(m, "engine"), engineWidth)}  {Pad(Prop(m, "name"), nameWidth)}  {Prop(m, "status")}{selected}");
            }

            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static async Task<int> TranscribeAsync(string baseUrl, CliOptions options, string? apiToken)
    {
        if (!string.IsNullOrEmpty(options.Language) && options.LanguageHints.Count > 0)
            return Error("--language and --language-hint cannot be used together.");

        var file = options.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file))
            return Error("Usage: typewhisper transcribe <file|->");

        if (file == "-")
        {
            byte[] audioBytes;
            await using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            await stdin.CopyToAsync(buffer);
            audioBytes = buffer.ToArray();
            if (audioBytes.Length == 0)
                return Error("No data received from stdin.");

            try
            {
                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(audioBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(fileContent, "file", CliRequestBuilder.BuildStdinFileName(audioBytes));

                AddString(content, "language", options.Language);
                foreach (var hint in options.LanguageHints)
                    AddString(content, "language_hint", hint);
                AddString(content, "task", options.Task);
                AddString(content, "target_language", options.TranslateTo);
                AddString(content, "engine", options.Engine);
                AddString(content, "model", options.Model);

                var path = options.AwaitDownload ? "/v1/transcribe?await_download=1" : "/v1/transcribe";
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}") { Content = content };
                CliRequestBuilder.ApplyApiToken(request, apiToken);
                var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return Error($"Transcription failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");

                if (options.Json) { Console.WriteLine(PrettyJson(body)); return 0; }

                using var doc = JsonDocument.Parse(body);
                Console.WriteLine(Prop(doc.RootElement, "text"));
                return 0;
            }
            catch (HttpRequestException)
            {
                return Error("TypeWhisper is not running or API server is disabled.");
            }
        }

        if (!File.Exists(file))
            return Error($"File not found: {file}");

        try
        {
            using var request = CliRequestBuilder.BuildTranscribeLocalFile(
                baseUrl,
                new CliTranscribeRequest(
                    Path.GetFullPath(file),
                    options.Language,
                    options.LanguageHints,
                    options.Task,
                    options.TranslateTo,
                    options.Engine,
                    options.Model,
                    options.AwaitDownload),
                apiToken);
            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return Error($"Transcription failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");

            if (options.Json) { Console.WriteLine(PrettyJson(body)); return 0; }

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
              transcribe <file|->       Transcribe an audio file, or - for stdin
              export <path>             Export a portable settings backup
              import <path>             Restore a portable settings backup

            Global options:
              --port <N>                API server port (default: auto-discover, fallback 8978)
              --api-token <token>       API token (overrides TYPEWHISPER_API_TOKEN and discovery)
              --json                    Output as JSON
              --version                 Show version
              --help, -h                Show this help

            Transcribe options:
              --language <code>         Source language (e.g. en, de)
              --language-hint <code>    Repeatable language hint for auto-detection
              --task <task>             transcribe (default) or translate
              --translate-to <code>     Target language for translation
              --engine <id>             Override the engine for this request
              --model <id>              Override the model for this request
              --await-download          Wait for local model restore/download

            Examples:
              typewhisper status
              typewhisper transcribe recording.wav
              typewhisper transcribe recording.wav --language de --json
              typewhisper transcribe recording.wav --language-hint de --language-hint en
              typewhisper transcribe recording.wav --engine groq --model whisper-large-v3-turbo
              typewhisper transcribe - < audio.wav
              typewhisper export typewhisper-backup.json
              typewhisper import typewhisper-backup.json
              typewhisper import typewhisper-backup.json --json
            """);
    }

    static void AddString(MultipartFormDataContent content, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            content.Add(new StringContent(value), name);
    }

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

    static string Prop(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message))
                    return message.GetString() ?? body;

                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? body;
            }
        }
        catch { }

        return body;
    }

    static string Pad(string value, int width) =>
        value.PadRight(width);

    static int Error(string message) { Console.Error.WriteLine($"Error: {message}"); return 1; }

    private sealed record CliOptions
    {
        /// <summary>
        /// Gets or sets the command value.
        /// </summary>
        public string? Command { get; private init; }
        /// <summary>
        /// Gets or sets the positionals value.
        /// </summary>
        public List<string> Positionals { get; private init; } = [];
        /// <summary>
        /// Gets or sets the port value.
        /// </summary>
        public int? Port { get; private init; }
        /// <summary>
        /// Gets or sets the api token value.
        /// </summary>
        public string? ApiToken { get; private init; }
        /// <summary>
        /// Gets or sets the json value.
        /// </summary>
        public bool Json { get; private init; }
        /// <summary>
        /// Gets or sets the show help value.
        /// </summary>
        public bool ShowHelp { get; private init; }
        /// <summary>
        /// Gets or sets the show version value.
        /// </summary>
        public bool ShowVersion { get; private init; }
        /// <summary>
        /// Gets or sets the language value.
        /// </summary>
        public string? Language { get; private init; }
        /// <summary>
        /// Gets or sets the language hints value.
        /// </summary>
        public List<string> LanguageHints { get; private init; } = [];
        /// <summary>
        /// Runs the task asynchronously..
        /// </summary>
        public string Task { get; private init; } = "transcribe";
        /// <summary>
        /// Gets or sets the translate to value.
        /// </summary>
        public string? TranslateTo { get; private init; }
        /// <summary>
        /// Gets or sets the engine value.
        /// </summary>
        public string? Engine { get; private init; }
        /// <summary>
        /// Gets or sets the model value.
        /// </summary>
        public string? Model { get; private init; }
        /// <summary>
        /// Gets or sets the await download value.
        /// </summary>
        public bool AwaitDownload { get; private init; }
        /// <summary>
        /// Gets or sets the error value.
        /// </summary>
        public string? Error { get; private init; }

        /// <summary>
        /// Parses the supplied value into the expected representation.
        /// </summary>
        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            var positionals = new List<string>();
            var languageHints = new List<string>();
            string? command = null;
            string? language = null;
            string task = "transcribe";
            string? translateTo = null;
            string? engine = null;
            string? model = null;
            int? port = null;
            string? apiToken = null;
            var json = false;
            var awaitDownload = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--help":
                    case "-h":
                        return options with { ShowHelp = true };
                    case "--version":
                        return options with { ShowVersion = true };
                    case "--json":
                        json = true;
                        break;
                    case "--await-download":
                        awaitDownload = true;
                        break;
                    case "--port":
                        if (!TryReadValue(args, ref i, out var portValue) || !int.TryParse(portValue, out var parsedPort))
                            return options with { Error = "--port requires a number." };
                        if (!CliConnectionResolver.IsPortInRange(parsedPort))
                            return options with { Error = "--port requires a TCP port between 1 and 65535." };
                        port = parsedPort;
                        break;
                    case "--api-token":
                        if (!TryReadValue(args, ref i, out apiToken))
                            return options with { Error = "--api-token requires a value." };
                        break;
                    case "--language":
                        if (!TryReadValue(args, ref i, out language))
                            return options with { Error = "--language requires a value." };
                        break;
                    case "--language-hint":
                        if (!TryReadValue(args, ref i, out var hint))
                            return options with { Error = "--language-hint requires a value." };
                        languageHints.Add(hint);
                        break;
                    case "--task":
                        if (!TryReadValue(args, ref i, out task))
                            return options with { Error = "--task requires a value." };
                        break;
                    case "--translate-to":
                        if (!TryReadValue(args, ref i, out translateTo))
                            return options with { Error = "--translate-to requires a value." };
                        break;
                    case "--engine":
                        if (!TryReadValue(args, ref i, out engine))
                            return options with { Error = "--engine requires a value." };
                        break;
                    case "--model":
                        if (!TryReadValue(args, ref i, out model))
                            return options with { Error = "--model requires a value." };
                        break;
                    default:
                        if (arg.StartsWith('-') && arg != "-")
                            return options with { Error = $"Unknown option '{arg}'." };

                        if (command is null)
                            command = arg;
                        else
                            positionals.Add(arg);
                        break;
                }
            }

            return options with
            {
                Command = command,
                Positionals = positionals,
                Port = port,
                ApiToken = apiToken,
                Json = json,
                Language = language,
                LanguageHints = languageHints,
                Task = task,
                TranslateTo = translateTo,
                Engine = engine,
                Model = model,
                AwaitDownload = awaitDownload
            };
        }

        private static bool TryReadValue(string[] args, ref int index, out string value)
        {
            if (index + 1 >= args.Length || LooksLikeOption(args[index + 1]))
            {
                value = "";
                return false;
            }

            value = args[++index];
            return true;
        }

        private static bool LooksLikeOption(string value) =>
            value.Length > 0 && value[0] == '-' && value != "-";
    }
}
