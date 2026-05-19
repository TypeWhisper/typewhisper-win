using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using TypeWhisper.Cli;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CliSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-cli-support-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConnectionResolver_UsesTokenizedDiscoveryFileBeforeLegacyPortFile()
    {
        var appDirectory = Path.Combine(_root, "TypeWhisper");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "api-port"), "9911");
        File.WriteAllText(Path.Combine(appDirectory, "api-discovery.json"), """
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
        var appDirectory = Path.Combine(_root, "TypeWhisper");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "api-discovery.json"), """
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
        var appDirectory = Path.Combine(_root, "TypeWhisper");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "api-port"), "9911");
        File.WriteAllText(Path.Combine(appDirectory, "api-discovery.json"), """
            {
              "version": 1,
              "port": 70000,
              "token": "token-from-discovery"
            }
            """);

        var legacyFallback = CliConnectionResolver.Resolve(new CliConnectionOptions(
            ApplicationDataRoot: _root));

        File.WriteAllText(Path.Combine(appDirectory, "api-port"), "0");
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
        var filePath = Path.Combine(_root, "large.wav");
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
