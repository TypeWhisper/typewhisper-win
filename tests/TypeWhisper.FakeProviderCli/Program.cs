using System.Diagnostics;
using System.Text.Json;

if (args.Contains("--child", StringComparer.Ordinal))
{
    await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "child.pid"), Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "codex").ToLowerInvariant();
var scenario = new DirectoryInfo(AppContext.BaseDirectory).Name.ToLowerInvariant();

if (args.Contains("--version", StringComparer.Ordinal) || args.Contains("-v", StringComparer.Ordinal))
{
    Console.WriteLine($"{executableName} 2.1.205");
    return;
}

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("--ignore-user-config --ignore-rules --ephemeral --output-schema --strict-config --json --sandbox --skip-git-repo-check");
    Console.WriteLine("--safe-mode --tools --strict-mcp-config --no-session-persistence --json-schema --disallowedTools --disable-slash-commands --no-chrome");
    Console.WriteLine("--print --output-format --json-schema");
    Console.WriteLine("--pure --model --agent --format --title --dir");
    return;
}

if (args.SequenceEqual(new[] { "auth", "list" }, StringComparer.Ordinal))
{
    if (scenario.Contains("signed-out", StringComparison.Ordinal)
        || scenario.Contains("opencode-auth-error", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("No OpenCode Zen credentials");
        Environment.ExitCode = 1;
        return;
    }

    if (scenario.Contains("opencode-auth-missing", StringComparison.Ordinal))
    {
        Console.WriteLine("GitHub Copilot");
        return;
    }

    var authentication = scenario.Contains("opencode-auth-ansi", StringComparison.Ordinal)
        ? "\u001b[32mOpenCode Zen\u001b[0m"
        : "OpenCode Zen";
    if (scenario.Contains("opencode-auth-stderr", StringComparison.Ordinal))
        Console.Error.WriteLine(authentication);
    else
        Console.WriteLine(authentication);
    return;
}

if (args.SequenceEqual(new[] { "login", "status" }, StringComparer.Ordinal)
    || args.SequenceEqual(new[] { "auth", "status" }, StringComparer.Ordinal))
{
    if (scenario.Contains("signed-out", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("not logged in");
        Environment.ExitCode = 1;
        return;
    }

    if (scenario.Contains("auth-unknown", StringComparison.Ordinal))
    {
        Console.WriteLine("{\"loggedIn\":false}");
        return;
    }

    Console.WriteLine(executableName.Contains("claude", StringComparison.Ordinal)
        ? "{\"loggedIn\":true,\"authMethod\":\"subscription\"}"
        : "Logged in using subscription");
    return;
}


if (args.SequenceEqual(new[] { "models", "opencode", "--verbose", "--pure" }, StringComparer.Ordinal))
{
    if (scenario.Contains("catalog-fail", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("catalog unavailable");
        Environment.ExitCode = 2;
        return;
    }

    WriteModel(
        "paid-model",
        "Paid Model",
        inputCost: 1,
        outputCost: 2,
        cacheCost: 0);
    if (!scenario.Contains("catalog-none", StringComparison.Ordinal))
    {
        WriteModel(
            "muse-spark-1.3-contributor-free",
            "Muse Spark 1.3 Free",
            inputCost: 0,
            outputCost: 0,
            cacheCost: 0);
    }
    return;
}

var standardInput = await Console.In.ReadToEndAsync();
var capture = new
{
    arguments = args,
    standardInput,
    workingDirectory = Environment.CurrentDirectory,
    environment = Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString())
};
await File.WriteAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "capture.json"),
    JsonSerializer.Serialize(capture));

if (scenario.Contains("invalid-json", StringComparison.Ordinal))
{
    Console.WriteLine("{not-json");
    return;
}

if (scenario.Contains("invalid-utf8", StringComparison.Ordinal))
{
    await using var output = Console.OpenStandardOutput();
    await output.WriteAsync(new byte[] { 0xff, 0xfe, 0xfd });
    await output.FlushAsync();
    return;
}

if (scenario.Contains("timeout", StringComparison.Ordinal))
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (scenario.Contains("crash", StringComparison.Ordinal))
{
    Environment.ExitCode = 42;
    return;
}

if (scenario.Contains("huge-output", StringComparison.Ordinal))
{
    Console.Write(new string('x', 1024 * 1024 + 8192));
    Console.Error.Write(new string('e', 64 * 1024));
    return;
}

if (scenario.Contains("instant-child", StringComparison.Ordinal))
{
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--child" }
    });
    await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "spawned.pid"), child!.Id.ToString());
    return;
}

if (scenario.Contains("child", StringComparison.Ordinal))
{
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--child" }
    });
    await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "parent.pid"), Environment.ProcessId.ToString());
    await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, "spawned.pid"), child!.Id.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (scenario.Contains("auth-error", StringComparison.Ordinal))
{
    Console.Error.WriteLine("not logged in; login required");
    Environment.ExitCode = 3;
    return;
}

if (scenario.Contains("rate-limit", StringComparison.Ordinal))
{
    Console.Error.WriteLine("rate limit exceeded");
    Environment.ExitCode = 4;
    return;
}

if (scenario.Contains("network-auth-model", StringComparison.Ordinal))
{
    Console.Error.WriteLine("authentication service unreachable while resolving model");
    Environment.ExitCode = 5;
    return;
}

if (scenario.Contains("stderr", StringComparison.Ordinal))
    Console.Error.Write(new string('w', 48 * 1024));

var logicalResult = JsonSerializer.Serialize(new { text = "processed" });
if (executableName.Contains("codex", StringComparison.Ordinal))
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        type = "item.completed",
        item = new { type = "agent_message", text = logicalResult }
    }));
    Console.WriteLine("{\"type\":\"turn.completed\"}");
}
else if (executableName.Contains("claude", StringComparison.Ordinal))
{
    Console.WriteLine("{\"type\":\"result\",\"subtype\":\"success\",\"structured_output\":{\"text\":\"processed\"}}");
}
else if (executableName.Contains("opencode", StringComparison.Ordinal))
{
    Console.WriteLine("{\"type\":\"step_start\",\"part\":{\"type\":\"step-start\"}}");
    Console.WriteLine("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"processed\\\"}\"}}");
}
else
{
    Console.WriteLine("{\"type\":\"init\"}");
    Console.WriteLine("{\"type\":\"result\",\"structured_output\":{\"text\":\"processed\"}}");
}

static void WriteModel(
    string id,
    string name,
    double inputCost,
    double outputCost,
    double cacheCost)
{
    Console.WriteLine($"opencode/{id}");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        id,
        providerID = "opencode",
        name,
        status = "active",
        capabilities = new
        {
            input = new { text = true, image = false },
            output = new { text = true, image = false }
        },
        cost = new
        {
            input = inputCost,
            output = outputCost,
            cache = new { read = cacheCost, write = 0 }
        },
        variants = new { low = new { }, high = new { } }
    }, new JsonSerializerOptions { WriteIndented = true }));
}
