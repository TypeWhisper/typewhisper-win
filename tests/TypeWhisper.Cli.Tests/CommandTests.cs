using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public class CommandTests
{
    private static async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        using var process = Start(args);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return (process.ExitCode, await stdout, await stderr);
    }

    private static Process Start(string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TypeWhisper.Cli.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Remove("TYPEWHISPER_API_TOKEN");
        start.Environment.Remove("TYPEWHISPER_PROFILE");
        return Process.Start(start)!;
    }

    [Theory]
    [InlineData("status extra")]
    [InlineData("status --dev --profile example")]
    [InlineData("status --profile example --dev")]
    [InlineData("status --no-corrections")]
    [InlineData("models load --engine whisper")]
    [InlineData("dictation stop extra")]
    [InlineData("dictation result invalid")]
    [InlineData("history --limit 201")]
    [InlineData("history --offset -1")]
    [InlineData("history search test --query other")]
    [InlineData("status --workflow test")]
    [InlineData("transcribe one.wav two.wav")]
    [InlineData("transcribe one.wav --task invalid")]
    public async Task InvalidArgumentsFailBeforeConnecting(string arguments)
    {
        var result = await RunAsync(arguments.Split(' '));
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.StartsWith("Error:", result.Error);
        Assert.DoesNotContain("not running", result.Error);
        Assert.DoesNotContain(" at ", result.Error);
    }

    [Theory]
    [InlineData("dictation start --workflow workflow-1", "POST", "/v1/dictation/start", "{\"workflow_id\":\"workflow-1\"}")]
    [InlineData("dictation stop", "POST", "/v1/dictation/stop", "")]
    [InlineData("dictation status", "GET", "/v1/dictation/status", "")]
    [InlineData("dictation result 00000000-0000-0000-0000-000000000001", "GET", "/v1/dictation/transcription?id=00000000-0000-0000-0000-000000000001", "")]
    [InlineData("history search hello --limit 3 --offset 2", "GET", "/v1/history?limit=3&offset=2&q=hello", "")]
    [InlineData("history --query hello", "GET", "/v1/history?limit=50&offset=0&q=hello", "")]
    [InlineData("history last", "GET", "/v1/history?limit=1&offset=0", "")]
    [InlineData("last", "GET", "/v1/history?limit=1&offset=0", "")]
    [InlineData("models load --engine whisper --model large", "POST", "/v1/models/load", "{\"engine\":\"whisper\",\"model\":\"large\"}")]
    [InlineData("models unload --engine whisper", "POST", "/v1/models/unload", "{\"engine\":\"whisper\",\"model\":null}")]
    [InlineData("models delete --engine whisper --model large", "DELETE", "/v1/models?engine=whisper&model=large", "")]
    [InlineData("status", "GET", "/v1/status", "")]
    [InlineData("status --dev", "GET", "/v1/status", "")]
    [InlineData("models", "GET", "/v1/models", "")]
    public async Task SendsApiContractWithTokenAndJsonStdout(string args, string method, string path, string expectedBody)
    {
        using var server = Listen(out var port);
        var run = RunAsync([..args.Split(' '), "--port", port.ToString(), "--api-token", "test-token", "--json"]);
        var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(method, context.Request.HttpMethod);
        Assert.Equal(path, context.Request.RawUrl);
        Assert.Equal("Bearer test-token", context.Request.Headers["Authorization"]);
        Assert.Equal(expectedBody, await new StreamReader(context.Request.InputStream).ReadToEndAsync());
        await Reply(context, 200, "{\"status\":\"ready\",\"entries\":[],\"models\":[]}");
        var result = await run;
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AcceptedImportDoesNotClaimCompletedRestore()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{}");
            using var server = Listen(out var port);
            var run = RunAsync("import", file, "--port", port.ToString());
            var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Reply(context, 202, "{\"accepted\":true,\"restart_required\":true}");
            var result = await run;
            Assert.Equal(0, result.Exit);
            Assert.Contains("accepted", result.Output);
            Assert.Contains("restarting", result.Output);
            Assert.DoesNotContain("Backup restored", result.Output);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(401, "{\"error\":{\"message\":\"Unauthorized\"}}", "Unauthorized")]
    [InlineData(200, "invalid json", "Error:")]
    [InlineData(302, "redirect", "302")]
    public async Task ApiFailuresProduceCleanErrors(int status, string body, string error)
    {
        using var server = Listen(out var port);
        var run = RunAsync("dictation", "status", "--port", port.ToString(), "--json");
        var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Reply(context, status, body);
        var result = await run;
        Assert.Equal(3, result.Exit);
        Assert.Empty(result.Output);
        Assert.Contains(error, result.Error);
        Assert.DoesNotContain(" at ", result.Error);
    }

    [Theory]
    [InlineData(0, "No data received")]
    [InlineData(32 * 1024 * 1024 + 1, "32 MiB")]
    public async Task StdinIsBoundedBeforeRequest(int bytes, string error)
    {
        using var process = Start(["transcribe", "-", "--port", "1"]);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (bytes > 0) await process.StandardInput.BaseStream.WriteAsync(new byte[bytes]);
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Contains(error, await stderr);
    }

    [Fact]
    public async Task LocalTranscriptionUsesAbsoluteFilePathAndOmitsEmptyHints()
    {
        var file = Path.GetTempFileName();
        try
        {
            using var server = Listen(out var port);
            var run = RunAsync("transcribe", file, "--language", "de", "--port", port.ToString());
            var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("POST", context.Request.HttpMethod);
            Assert.Equal("/v1/transcribe/local-file", context.Request.RawUrl);
            using var payload = JsonDocument.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync());
            Assert.Equal(Path.GetFullPath(file), payload.RootElement.GetProperty("path").GetString());
            Assert.Equal("de", payload.RootElement.GetProperty("language").GetString());
            Assert.False(payload.RootElement.TryGetProperty("language_hints", out _));
            await Reply(context, 200, "{\"text\":\"Guten Tag\"}");
            var result = await run;
            Assert.Equal(0, result.Exit);
            Assert.Equal("Guten Tag", result.Output.Trim());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ExportWritesResponseToFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            using var server = Listen(out var port);
            var run = RunAsync("export", file, "--port", port.ToString(), "--json");
            var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("/v1/settings/export", context.Request.RawUrl);
            await Reply(context, 200, "{\"schema\":1}");
            var result = await run;
            Assert.Equal(0, result.Exit);
            Assert.Equal("{\"schema\":1}", await File.ReadAllTextAsync(file));
            using var output = JsonDocument.Parse(result.Output);
            Assert.Equal(Path.GetFullPath(file), output.RootElement.GetProperty("file").GetString());
            Assert.Equal(12, output.RootElement.GetProperty("bytes").GetInt32());
            Assert.Equal(new[] { "bytes", "file" }, output.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscribeAcceptsImplicitAndExplicitStdin(bool explicitDash)
    {
        using var server = Listen(out var port);
        using var process = Start(explicitDash
            ? ["transcribe", "-", "--port", port.ToString()]
            : ["transcribe", "--port", port.ToString()]);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.BaseStream.WriteAsync("RIFF0000WAVEexample"u8.ToArray());
        process.StandardInput.Close();
        var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("/v1/transcribe", context.Request.RawUrl);
        var multipart = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
        Assert.Contains("RIFF0000WAVEexample", multipart);
        await Reply(context, 200, "{\"text\":\"Hello\"}");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("Hello", (await stdout).Trim());
        Assert.Empty(await stderr);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("models")]
    [InlineData("transcribe")]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("history")]
    public async Task ConnectionFailuresReturnMacExitCode(string command)
    {
        var file = Path.GetTempFileName();
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        try
        {
            await File.WriteAllTextAsync(file, "{}");
            string[] args = command is "transcribe" or "export" or "import" ? [command, file] : [command];
            var result = await RunAsync([.. args, "--port", port.ToString()]);
            Assert.Equal(2, result.Exit);
            Assert.Empty(result.Output);
            Assert.Contains("not running", result.Error);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("status", 401, "{}")]
    [InlineData("models", 503, "{}")]
    [InlineData("transcribe", 409, "{}")]
    [InlineData("export", 500, "{}")]
    [InlineData("import", 400, "{}")]
    [InlineData("status", 200, "invalid json")]
    [InlineData("export", 200, "invalid json")]
    [InlineData("import", 200, "invalid json")]
    public async Task ServerFailuresReturnMacExitCode(string command, int status, string response)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{}");
            using var server = Listen(out var port);
            string[] args = command is "transcribe" or "export" or "import" ? [command, file] : [command];
            var run = RunAsync([.. args, "--port", port.ToString(), "--json"]);
            var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Reply(context, status, response);
            var result = await run;
            Assert.Equal(3, result.Exit);
            Assert.Empty(result.Output);
            Assert.StartsWith("Error:", result.Error);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task InvalidBackupRemainsAnInputError()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "not json");
            var result = await RunAsync("import", file, "--port", "1");
            Assert.Equal(1, result.Exit);
            Assert.Contains("Invalid backup JSON", result.Error);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptionForwardsMacOptionsForFileAndStdin(bool stdin)
    {
        var file = Path.GetTempFileName();
        try
        {
            using var server = Listen(out var port);
            using var process = Start(["transcribe", stdin ? "-" : file, "--port", port.ToString(),
                "--no-corrections", "--language-hint", "de", "--language-hint", "en",
                "--translate-to", "fr", "--engine", "engine-id", "--model", "model-id", "--await-download"]);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (stdin) await process.StandardInput.BaseStream.WriteAsync("RIFF0000WAVEexample"u8.ToArray());
            process.StandardInput.Close();
            var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(stdin ? "/v1/transcribe?await_download=1" : "/v1/transcribe/local-file?await_download=1", context.Request.RawUrl);
            var payload = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
            if (stdin)
            {
                foreach (var (name, value) in new[] { ("apply_corrections", "false"), ("language_hint", "de"),
                    ("language_hint", "en"), ("target_language", "fr"), ("engine", "engine-id"), ("model", "model-id") })
                    Assert.Contains($"name={name}\r\n\r\n{value}\r\n", payload);
                Assert.True(payload.IndexOf("\r\nde\r\n", StringComparison.Ordinal) < payload.IndexOf("\r\nen\r\n", StringComparison.Ordinal));
            }
            else
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                Assert.Equal(new[] { "apply_corrections", "engine", "language_hints", "model", "path", "target_language", "task" },
                    root.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
                Assert.False(root.GetProperty("apply_corrections").GetBoolean());
                Assert.Equal(new[] { "de", "en" }, root.GetProperty("language_hints").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal("fr", root.GetProperty("target_language").GetString());
                Assert.Equal("engine-id", root.GetProperty("engine").GetString());
                Assert.Equal("model-id", root.GetProperty("model").GetString());
            }
            await Reply(context, 200, "{\"text\":\"Bonjour\"}");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("Bonjour", (await stdout).Trim());
            Assert.Empty(await stderr);
        }
        finally { File.Delete(file); }
    }

    private static HttpListener Listen(out int port)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static async Task Reply(HttpListenerContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
