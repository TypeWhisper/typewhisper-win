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
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly CancellationTokenSource Cancellation = new();

    static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Cancellation.Cancel(); };
        try { return await RunAsync(args); }
        catch (OperationCanceledException)
        { return Error(Cancellation.IsCancellationRequested ? "Operation cancelled." : "Request timed out.", Cancellation.IsCancellationRequested ? 1 : 2); }
        catch (HttpRequestException) { return Error("TypeWhisper is not running or API server is disabled.", 2); }
        catch (JsonException) { return Error("Invalid response from server.", 3); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Error(ex.Message); }
    }

    static async Task<int> RunAsync(string[] args)
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

        if (Validate(options) is { } validationError) return Error(validationError);

        var connection = CliConnectionResolver.Resolve(new CliConnectionOptions(
            PortOverride: options.Port,
            ApiTokenOverride: options.ApiToken,
            EnvironmentApiToken: Environment.GetEnvironmentVariable("TYPEWHISPER_API_TOKEN"),
            ProfileDirectory: options.DevMode ? null : options.ProfileDirectory ?? Environment.GetEnvironmentVariable("TYPEWHISPER_PROFILE"),
            DevMode: options.DevMode));
        var baseUrl = $"http://127.0.0.1:{connection.Port}";

        return options.Command switch
        {
            "status" => await StatusAsync(baseUrl, options.Json, connection.ApiToken),
            "models" when options.Positionals.Count == 0 || options.Positionals[0] == "list" => await ModelsAsync(baseUrl, options.Json, connection.ApiToken),
            "models" or "dictation" or "history" or "last" => await ExtendedAsync(baseUrl, options, connection.ApiToken),
            "transcribe" => await TranscribeAsync(baseUrl, options, connection.ApiToken),
            "export" => await ExportSettingsAsync(baseUrl, options, connection.ApiToken),
            "import" => await ImportSettingsAsync(baseUrl, options, connection.ApiToken),
            _ => Error($"Unknown command: {options.Command}")
        };
    }

    static string? Validate(CliOptions o)
    {
        var p = o.Positionals;
        var action = p.FirstOrDefault();
        var valid = o.Command switch
        {
            "status" or "last" => p.Count == 0,
            "export" or "import" => p.Count == 1,
            "transcribe" => p.Count <= 1,
            "models" => p.Count == 0 || p.Count == 1 && action is "list" or "load" or "unload" or "delete",
            "dictation" => p.Count == 1 && action is "start" or "stop" or "status" || p.Count == 2 && action == "result" && Guid.TryParse(p[1], out _),
            "history" => p.Count == 0 || p.Count == 1 && action == "last" || p.Count == 2 && action == "search",
            _ => false
        };
        if (!valid) return $"Invalid command or arguments for '{o.Command}'. Run typewhisper --help for usage.";
        if (o.DevMode && o.ProfileDirectory is not null) return "--dev and --profile cannot be used together.";
        var allowed = new HashSet<string> { "--port", "--profile", "--dev", "--api-token", "--json" };
        if (o.Command == "transcribe") allowed.UnionWith(["--language", "--language-hint", "--task", "--translate-to", "--engine", "--model", "--await-download", "--no-corrections"]);
        if (o.Command == "models" && action is "load" or "unload" or "delete") allowed.UnionWith(["--engine", "--model"]);
        if (o.Command == "dictation" && action == "start") allowed.Add("--workflow");
        if (o.Command == "history" && action != "last") allowed.UnionWith(["--query", "--limit", "--offset"]);
        if (o.Flags.FirstOrDefault(f => !allowed.Contains(f)) is { } invalid) return $"{invalid} is not valid for this command.";
        if (o.Command == "models" && action is "load" or "unload" or "delete" &&
            (string.IsNullOrWhiteSpace(o.Engine) || action != "unload" && string.IsNullOrWhiteSpace(o.Model)))
            return action == "unload" ? "models unload requires --engine." : $"models {action} requires --engine and --model.";
        if (o.Command == "history" && action == "search" && o.Query != null) return "Use either history search <query> or --query.";
        if (o.Command == "transcribe" && o.Task is not ("transcribe" or "translate")) return "--task must be transcribe or translate.";
        return null;
    }

    static async Task<int> ExtendedAsync(string baseUrl, CliOptions o, string? apiToken)
    {
        var action = o.Positionals.FirstOrDefault();
        var method = HttpMethod.Get;
        string path;
        object? payload = null;
        if (o.Command == "models")
        {
            method = action == "delete" ? HttpMethod.Delete : HttpMethod.Post;
            path = action == "delete"
                ? $"/v1/models?engine={Uri.EscapeDataString(o.Engine!)}&model={Uri.EscapeDataString(o.Model!)}"
                : $"/v1/models/{action}";
            if (action != "delete") payload = new { engine = o.Engine, model = o.Model };
        }
        else if (o.Command == "dictation")
        {
            path = action == "result" ? $"/v1/dictation/transcription?id={Uri.EscapeDataString(o.Positionals[1])}" : $"/v1/dictation/{action}";
            if (action is "start" or "stop") method = HttpMethod.Post;
            if (action == "start" && o.Workflow != null) payload = new { workflow_id = o.Workflow };
        }
        else
        {
            var last = o.Command == "last" || action == "last";
            var query = action == "search" ? o.Positionals[1] : o.Query;
            path = $"/v1/history?limit={(last ? 1 : o.Limit ?? 50)}&offset={o.Offset ?? 0}";
            if (query != null) path += $"&q={Uri.EscapeDataString(query)}";
        }
        using var request = new HttpRequestMessage(method, baseUrl + path);
        CliRequestBuilder.ApplyApiToken(request, apiToken);
        if (payload != null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, Cancellation.Token);
        var body = await response.Content.ReadAsStringAsync(Cancellation.Token);
        if (!response.IsSuccessStatusCode) return Error($"Request failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}", 3);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (o.Json) Console.WriteLine(PrettyJson(body));
        else if (o.Command is "history" or "last")
        {
            var entries = root.GetProperty("entries");
            if (entries.GetArrayLength() == 0) Console.WriteLine("No history entries.");
            foreach (var entry in entries.EnumerateArray())
                Console.WriteLine(o.Command == "last" || action == "last" ? Prop(entry, "text") : $"{Prop(entry, "timestamp")}  {Prop(entry, "text")}");
        }
        else if (o.Command == "models") Console.WriteLine($"{Prop(root, "status")}: {Prop(root, "engine")} {Prop(root, "model")}".TrimEnd());
        else if (action == "status") Console.WriteLine(Prop(root, "state"));
        else if (action == "result" && root.TryGetProperty("transcription", out var transcript) && transcript.ValueKind == JsonValueKind.Object)
            Console.WriteLine(Prop(transcript, "text"));
        else Console.WriteLine($"{Prop(root, "status")}: {Prop(root, "id")}");
        if (o.Command == "dictation" && action == "result" && Prop(root, "status") == "failed")
            return Error(Prop(root, "error") is { Length: > 0 } error ? error : "Dictation failed.", 3);
        return 0;
    }

    static async Task<int> ExportSettingsAsync(string baseUrl, CliOptions options, string? apiToken)
    {
        if (options.Positionals.Count != 1 || string.IsNullOrWhiteSpace(options.Positionals[0]))
            return Error("Usage: typewhisper export <path>");

        var path = Path.GetFullPath(options.Positionals[0]);
        try
        {
            using var request = CliRequestBuilder.BuildGet(baseUrl, "/v1/settings/export", apiToken);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Cancellation.Token);
            await using var responseStream = await response.Content.ReadAsStreamAsync();
            var body = await CliBackupFile.ReadBoundedUtf8Async(
                responseStream,
                response.Content.Headers.ContentLength);
            if (!response.IsSuccessStatusCode)
                return Error($"Backup export failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}", 3);

            using (JsonDocument.Parse(body)) { }
            await CliBackupFile.WriteAtomicAsync(path, body);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    file = path,
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
            return Error("TypeWhisper is not running or API server is disabled.", 2);
        }
        catch (JsonException) { return Error("Invalid response from server.", 3); }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or DecoderFallbackException)
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
            try { using (JsonDocument.Parse(backupJson)) { } }
            catch (JsonException ex) { return Error($"Invalid backup JSON: {ex.Message}"); }

            using var request = CliRequestBuilder.BuildSettingsImport(baseUrl, backupJson, apiToken);
            using var response = await Http.SendAsync(request, Cancellation.Token);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error($"Backup import failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}", 3);

            if (options.Json)
            {
                Console.WriteLine(PrettyJson(body));
                return ImportSucceeded(body) ? 0 : 3;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                Console.WriteLine("Backup import accepted. TypeWhisper is restarting to finish restoring it.");
                return 0;
            }
            return PrintImportSummary(body);
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.", 2);
        }
        catch (JsonException) { return Error("Invalid response from server.", 3); }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or DecoderFallbackException)
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

        return success ? 0 : 3;
    }

    static int Count(JsonElement result, string name) =>
        result.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;

    static async Task<int> StatusAsync(string baseUrl, bool json, string? apiToken)
    {
        try
        {
            using var request = CliRequestBuilder.BuildGet(baseUrl, "/v1/status", apiToken);
            using var response = await Http.SendAsync(request, Cancellation.Token);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error($"Status request failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}", 3);

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
            return Error("TypeWhisper is not running or API server is disabled.", 2);
        }
    }

    static async Task<int> ModelsAsync(string baseUrl, bool json, string? apiToken)
    {
        try
        {
            using var request = CliRequestBuilder.BuildGet(baseUrl, "/v1/models", apiToken);
            using var response = await Http.SendAsync(request, Cancellation.Token);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error($"Models request failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}", 3);

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
            return Error("TypeWhisper is not running or API server is disabled.", 2);
        }
    }

    static async Task<int> TranscribeAsync(string baseUrl, CliOptions options, string? apiToken)
    {
        if (!string.IsNullOrEmpty(options.Language) && options.LanguageHints.Count > 0)
            return Error("--language and --language-hint cannot be used together.");

        var file = options.Positionals.FirstOrDefault() ?? "-";

        if (file == "-")
        {
            byte[] audioBytes;
            await using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stdin.ReadAsync(chunk, Cancellation.Token)) > 0)
            {
                if (buffer.Length + read > 32 * 1024 * 1024)
                    return Error("Stdin audio exceeds the 32 MiB limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), Cancellation.Token);
            }
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
                if (!options.ApplyCorrections) AddString(content, "apply_corrections", "false");

                var path = options.AwaitDownload ? "/v1/transcribe?await_download=1" : "/v1/transcribe";
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}") { Content = content };
                CliRequestBuilder.ApplyApiToken(request, apiToken);
                using var response = await Http.SendAsync(request, Cancellation.Token);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return Error($"Transcription failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}", 3);

                if (options.Json) { Console.WriteLine(PrettyJson(body)); return 0; }

                using var doc = JsonDocument.Parse(body);
                Console.WriteLine(Prop(doc.RootElement, "text"));
                return 0;
            }
            catch (HttpRequestException)
            {
                return Error("TypeWhisper is not running or API server is disabled.", 2);
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
                    options.AwaitDownload,
                    options.ApplyCorrections),
                apiToken);
            using var response = await Http.SendAsync(request, Cancellation.Token);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return Error($"Transcription failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}", 3);

            if (options.Json) { Console.WriteLine(PrettyJson(body)); return 0; }

            using var doc = JsonDocument.Parse(body);
            Console.WriteLine(Prop(doc.RootElement, "text"));
            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.", 2);
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
            TypeWhisper CLI - Speech-to-Text from the command line

            Usage: typewhisper <command> [options]

            Commands:
              status                    Show TypeWhisper status
              models [list]             List available models
              models load|delete        Requires --engine <id> --model <id>
              models unload             Requires --engine <id>; optional --model <id>
              dictation start           Start recording; optional --workflow <id>
              dictation stop|status     Stop recording or show recording state
              dictation result <id>     Get a dictation session result
              history [search <query>]  List history; --query, --limit, --offset
              history last | last       Show the latest history entry
              transcribe [file|-]       Transcribe a file; omit it or use - for stdin
              export <path>             Export a portable settings backup
              import <path>             Restore a portable settings backup

            Global options:
              --port <N>                API server port (default: auto-discover, fallback 8978)
              --profile <directory>     Discover an explicit profile (or TYPEWHISPER_PROFILE)
              --dev                     Connect to the WinUI development profile
              --api-token <token>       API token (overrides TYPEWHISPER_API_TOKEN and discovery)
              --json                    Output as JSON
              --version                 Show version
              --help, -h                Show this help

            Transcribe options:
              --language <code>         Source language (e.g. en, de)
              --language-hint <code>    Repeatable ordered hint; requires engine support
              --task <task>             transcribe (default) or translate
              --no-corrections          Return raw text without dictionary corrections
              --translate-to <code>     Translate via the configured default workflow LLM
              --engine <id>             Temporarily select an engine for this request
              --model <id>              Temporarily select a model for this request
              --await-download          Wait for model restore/download before transcribing

            History options:
              --query <text>            Filter history
              --limit <N>               Maximum entries, 0-200 (default: 50)
              --offset <N>              Skip entries (default: 0)

            Examples:
              typewhisper status
              typewhisper transcribe recording.wav
              typewhisper transcribe recording.wav --language de --json
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
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
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

    static int Error(string message, int exitCode = 1) { Console.Error.WriteLine($"Error: {message}"); return exitCode; }

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
        public bool ApplyCorrections { get; private init; } = true;
        public bool DevMode { get; private init; }
        public string? ProfileDirectory { get; private init; }
        public string? Workflow { get; private init; }
        public string? Query { get; private init; }
        public int? Limit { get; private init; }
        public int? Offset { get; private init; }
        public HashSet<string> Flags { get; private init; } = [];
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
            var applyCorrections = true;
            var devMode = false;
            string? workflow = null, query = null, profile = null;
            int? limit = null, offset = null;
            var flags = new HashSet<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--")) flags.Add(arg);
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
                    case "--dev":
                        devMode = true;
                        break;
                    case "--no-corrections":
                        applyCorrections = false;
                        break;
                    case "--profile":
                        if (!TryReadValue(args, ref i, out profile))
                            return options with { Error = "--profile requires a directory." };
                        break;
                    case "--workflow":
                        if (!TryReadValue(args, ref i, out workflow))
                            return options with { Error = "--workflow requires a value." };
                        break;
                    case "--query":
                        if (!TryReadValue(args, ref i, out query))
                            return options with { Error = "--query requires a value." };
                        break;
                    case "--limit":
                    case "--offset":
                        if (!TryReadValue(args, ref i, out var number) || !int.TryParse(number, out var parsed) || parsed < 0 || (arg == "--limit" && parsed > 200))
                            return options with { Error = arg == "--limit" ? "--limit requires a number between 0 and 200." : "--offset requires a nonnegative integer." };
                        if (arg == "--limit") limit = parsed; else offset = parsed;
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
                AwaitDownload = awaitDownload,
                ApplyCorrections = applyCorrections,
                DevMode = devMode,
                ProfileDirectory = profile, Workflow = workflow, Query = query, Limit = limit, Offset = offset, Flags = flags
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
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool LooksLikeOption(string value) =>
            value.Length > 0 && value[0] == '-' && value != "-";
    }
}
