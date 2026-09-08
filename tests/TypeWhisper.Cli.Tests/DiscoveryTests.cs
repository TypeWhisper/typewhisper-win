using System.Text.Json;
using TypeWhisper.Cli;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class DiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-cli-discovery-" + Guid.NewGuid().ToString("N"));
    private string Profile(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private void Discovery(string name, int port, bool? requires = null) => File.WriteAllText(Path.Combine(Profile(name), "api-discovery.json"),
        JsonSerializer.Serialize(new { version = 1, port, token = "synthetic-discovery-token", requires_authentication = requires }));

    [Fact]
    public void PrefersWinUiProfileAndDiscoversRequiredToken()
    {
        Discovery("TypeWhisper", 8978); Discovery("TypeWhisper-WinUI-DevUserData", 18979, true);
        var value = CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root));
        Assert.Equal(18979, value.Port); Assert.Equal("synthetic-discovery-token", value.ApiToken);
    }
    [Fact]
    public void ExplicitProfileIsIsolatedAndOptionalAuthenticationOmitsToken()
    {
        Discovery("fixture", 18980, false); Discovery("TypeWhisper", 8978, true);
        var value = CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, ProfileDirectory: Profile("fixture")));
        Assert.Equal(18980, value.Port); Assert.Null(value.ApiToken);
    }
    [Fact]
    public void PortOverrideDoesNotSendAnotherServersToken()
    {
        Discovery("TypeWhisper-WinUI-DevUserData", 18979, true);
        var value = CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, PortOverride: 18980));
        Assert.Equal(18980, value.Port); Assert.Null(value.ApiToken);
    }
    [Fact]
    public void MatchingPortSelectsMatchingProfileToken()
    {
        Discovery("TypeWhisper-WinUI-DevUserData", 18979, false); Discovery("TypeWhisper", 8978, true);
        var value = CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, PortOverride: 8978));
        Assert.Equal("synthetic-discovery-token", value.ApiToken);
    }
    [Fact]
    public void ExplicitTokenOverridesEnvironmentAndDiscovery()
    {
        Discovery("TypeWhisper", 8978, false);
        Assert.Equal("explicit", CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, ApiTokenOverride: "explicit", EnvironmentApiToken: "env")).ApiToken);
        Assert.Equal("env", CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, EnvironmentApiToken: "env")).ApiToken);
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":1,\"port\":70000,\"token\":\"secret\"}")]
    [InlineData("{\"version\":2,\"port\":18979,\"token\":\"secret\"}")]
    public void InvalidDiscoveryFallsBackWithoutToken(string json)
    {
        var profile = Profile("TypeWhisper"); File.WriteAllText(Path.Combine(profile, "api-discovery.json"), json);
        File.WriteAllText(Path.Combine(profile, "api-port"), "8979");
        var value = CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root));
        Assert.Equal(8979, value.Port); Assert.Null(value.ApiToken);
    }
    [Fact]
    public async Task OrdinaryLocalFileRequestOmitsUnsupportedEmptyHints()
    {
        using var request = CliRequestBuilder.BuildTranscribeLocalFile("http://127.0.0.1:8978",
            new("C:/Audio/example.wav", null, [], "transcribe", null, null, null, false), null);
        var body = await request.Content!.ReadAsStringAsync();
        Assert.DoesNotContain("language_hints", body); Assert.DoesNotContain("target_language", body);
        Assert.DoesNotContain("apply_corrections", body);
        Assert.Contains("example.wav", body);
    }
    [Fact]
    public void DevModeDoesNotFallBackToProductionDiscovery()
    {
        Discovery("TypeWhisper", 18981, true);
        var missing = CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, DevMode: true));
        Assert.Equal(8978, missing.Port);
        Assert.Null(missing.ApiToken);
        Discovery("TypeWhisper-WinUI-DevUserData", 18982, true);
        var dev = CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, DevMode: true));
        Assert.Equal(18982, dev.Port);
        Assert.Equal("synthetic-discovery-token", dev.ApiToken);
    }

    [Fact]
    public void DevModeRejectsExplicitProfile()
    {
        Assert.Throws<ArgumentException>(() => CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root,
            ProfileDirectory: Profile("custom"), DevMode: true)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
