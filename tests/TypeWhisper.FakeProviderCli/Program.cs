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
    Console.WriteLine("--ignore-user-config --ignore-rules --ephemeral --output-schema --strict-config --json");
    Console.WriteLine("--safe-mode --tools --strict-mcp-config --no-session-persistence --json-schema");
    Console.WriteLine("--print --output-format --json-schema");
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
    await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xff, 0xfe, 0xfd });
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
else
{
    Console.WriteLine("{\"type\":\"init\"}");
    Console.WriteLine("{\"type\":\"result\",\"structured_output\":{\"text\":\"processed\"}}");
}
