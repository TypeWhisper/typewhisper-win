using System.Net.Http.Headers;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.Cli;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CliSupportTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "typewhisper-cli-support-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConnectionResolver_UsesTokenizedDiscoveryFileBeforeLegacyPortFile()
    {
        var appDirectory = Path.Join(_root, "TypeWhisper");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Join(appDirectory, "api-port"), "9911");
        File.WriteAllText(Path.Join(appDirectory, "api-discovery.json"), """
            {
              "version": 1,
              "port": 9922,
              "token": "token-from-discovery"
            }
            """);

        var connection = CliConnectionResolver.Resolve(new CliConnectionOptions(ApplicationDataRoot: _root));

        Assert.Equal(9922, connection.Port);
        Assert.Equal("token-from-discovery", connection.ApiToken);
    }

    [Fact]
    public void ConnectionResolver_UsesExplicitAndEnvironmentOverrides()
    {
        var appDirectory = Path.Join(_root, "TypeWhisper");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Join(appDirectory, "api-discovery.json"), """
            {
              "version": 1,
              "port": 9922,
              "token": "token-from-discovery"
            }
            """);

        var envOverride = CliConnectionResolver.Resolve(new CliConnectionOptions(
            ApplicationDataRoot: _root,
            EnvironmentApiToken: "token-from-env"));
        var explicitOverride = CliConnectionResolver.Resolve(new CliConnectionOptions(
            ApplicationDataRoot: _root,
            PortOverride: 9933,
            ApiTokenOverride: "token-from-flag",
            EnvironmentApiToken: "token-from-env"));

        Assert.Equal(9922, envOverride.Port);
        Assert.Equal("token-from-env", envOverride.ApiToken);
        Assert.Equal(9933, explicitOverride.Port);
        Assert.Equal("token-from-flag", explicitOverride.ApiToken);
    }

    [Fact]
    public void ConnectionResolver_SkipsInvalidPorts()
    {
        var appDirectory = Path.Join(_root, "TypeWhisper");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Join(appDirectory, "api-port"), "9911");
        File.WriteAllText(Path.Join(appDirectory, "api-discovery.json"), """
            {
              "version": 1,
              "port": 70000,
              "token": "token-from-discovery"
            }
            """);

        var legacyFallback = CliConnectionResolver.Resolve(new CliConnectionOptions(
            ApplicationDataRoot: _root));

        File.WriteAllText(Path.Join(appDirectory, "api-port"), "0");
        var defaultFallback = CliConnectionResolver.Resolve(new CliConnectionOptions(
            ApplicationDataRoot: _root,
            PortOverride: -1));

        Assert.Equal(9911, legacyFallback.Port);
        Assert.Equal(8978, defaultFallback.Port);
    }


    [Fact]
    public void RequestBuilder_AddsBearerTokenToRequests()
    {
        using var request = CliRequestBuilder.BuildGet("http://127.0.0.1:8978", "/v1/models", "cli-token");

        Assert.Equal(new Uri("http://127.0.0.1:8978/v1/models"), request.RequestUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "cli-token"), request.Headers.Authorization);
    }

    [Fact]
    public async Task RequestBuilder_UsesLocalFileEndpointWithoutUploadingBytes()
    {
        var filePath = Path.Join(_root, "large.wav");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(filePath, "distinctive-audio-bytes");

        using var request = CliRequestBuilder.BuildTranscribeLocalFile(
            "http://127.0.0.1:8978",
            new CliTranscribeRequest(
                FilePath: filePath,
                Language: null,
                LanguageHints: ["de", "en"],
                Task: "transcribe",
                TargetLanguage: null,
                Engine: "mock",
                Model: "tiny",
                AwaitDownload: true),
            apiToken: "cli-token");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("http://127.0.0.1:8978/v1/transcribe/local-file?await_download=1"), request.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "cli-token"), request.Headers.Authorization);
        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

        var body = await request.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(filePath, root.GetProperty("path").GetString());
        Assert.Equal(["de", "en"], root.GetProperty("language_hints").EnumerateArray().Select(e => e.GetString() ?? "").ToArray());
        Assert.Equal("transcribe", root.GetProperty("task").GetString());
        Assert.Equal("mock", root.GetProperty("engine").GetString());
        Assert.Equal("tiny", root.GetProperty("model").GetString());
        Assert.DoesNotContain("distinctive-audio-bytes", body);
    }

    [Fact]
    public async Task RequestBuilder_BuildsAuthenticatedSettingsImport()
    {
        const string backup = "{\"format\":\"typewhisper-backup\"}";

        using var request = CliRequestBuilder.BuildSettingsImport(
            "http://127.0.0.1:8978",
            backup,
            "cli-token");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("http://127.0.0.1:8978/v1/settings/import"), request.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "cli-token"), request.Headers.Authorization);
        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
        Assert.Equal(backup, await request.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task BackupFile_AtomicallyCreatesAndReplacesTarget()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, "backup.json");

        await CliBackupFile.WriteAtomicAsync(path, "{\"version\":1}");
        await CliBackupFile.WriteAtomicAsync(path, "{\"version\":2}");

        Assert.Equal("{\"version\":2}", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, ".backup.json.*.tmp"));
    }

    [Fact]
    public async Task BackupFile_ReadsUtf8AndRejectsOversizedFiles()
    {
        Directory.CreateDirectory(_root);
        var validPath = Path.Join(_root, "valid.json");
        await File.WriteAllTextAsync(validPath, "{\"text\":\"Grüße\"}");

        Assert.Equal("{\"text\":\"Grüße\"}", await CliBackupFile.ReadAsync(validPath));

        var oversizedPath = Path.Join(_root, "oversized.json");
        await using (var stream = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(CliBackupFile.MaxBackupBytes + 1L);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CliBackupFile.ReadAsync(oversizedPath));
        Assert.Contains("64 MiB", error.Message);
    }

    [Fact]
    public async Task BackupFile_ReadsResponseIncrementallyAndRejectsBeforeLimitIsExceeded()
    {
        await using var valid = new MemoryStream(Encoding.UTF8.GetBytes("Grüße"));
        Assert.Equal("Grüße", await CliBackupFile.ReadBoundedUtf8Async(valid, valid.Length, 16));

        await using var oversized = new MemoryStream([1, 2, 3, 4, 5]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CliBackupFile.ReadBoundedUtf8Async(oversized, declaredLength: null, maxBytes: 4));

        await using var invalidUtf8 = new MemoryStream([0xC3, 0x28]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            CliBackupFile.ReadBoundedUtf8Async(invalidUtf8, invalidUtf8.Length, 16));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportCommand_ReportsInvalidBackupInputAsCliError(bool oversized)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, oversized ? "oversized.json" : "invalid-utf8.json");
        if (oversized)
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.SetLength(CliBackupFile.MaxBackupBytes + 1L);
        }
        else
        {
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(CliBackupFile).Assembly.Location);
        startInfo.ArgumentList.Add("import");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the CLI test process.");
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Error: Could not import backup:", await standardError);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E' }, "stdin.wav")]
    [InlineData(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C' }, "stdin.flac")]
    [InlineData(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S' }, "stdin.ogg")]
    [InlineData(new byte[] { 0xFF, 0xF1 }, "stdin.aac")]
    [InlineData(new byte[] { 0xFF, 0xF9 }, "stdin.aac")]
    [InlineData(new byte[] { (byte)'I', (byte)'D', (byte)'3' }, "stdin.mp3")]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, "stdin.mp3")]
    public void RequestBuilder_DetectsStdinFileNameFromAudioHeader(byte[] audioBytes, string expectedFileName)
    {
        Assert.Equal(expectedFileName, CliRequestBuilder.BuildStdinFileName(audioBytes));
    }

    [Fact]
    public void CliOptions_RejectsApiTokenWhenNextArgumentIsSwitch()
    {
        var options = ParseCliOptions("status", "--api-token", "--json");

        Assert.Equal("--api-token requires a value.", GetOptionValue<string>(options, "Error"));
    }

    [Fact]
    public void CliOptions_RejectsSwitchLikeValueForSharedOptionParser()
    {
        var options = ParseCliOptions("transcribe", "file.wav", "--language", "--json");

        Assert.Equal("--language requires a value.", GetOptionValue<string>(options, "Error"));
    }

    [Theory]
    [InlineData("export", "backup.json")]
    [InlineData("import", "backup.json")]
    public void CliOptions_ParsesSettingsBackupCommands(string command, string path)
    {
        var options = ParseCliOptions(command, path, "--json");

        Assert.Equal(command, GetOptionValue<string>(options, "Command"));
        Assert.Equal([path], GetOptionValue<List<string>>(options, "Positionals"));
        Assert.True(GetOptionValue<bool>(options, "Json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static object ParseCliOptions(params string[] args)
    {
        var type = typeof(CliRequestBuilder).Assembly.GetType("TypeWhisper.Cli.Program+CliOptions", throwOnError: true)!;
        var parse = type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
        return parse.Invoke(null, [args])!;
    }

    private static T? GetOptionValue<T>(object options, string propertyName) =>
        (T?)options.GetType()
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(options);
}
