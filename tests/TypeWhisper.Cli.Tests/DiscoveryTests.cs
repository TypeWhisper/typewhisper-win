using System.Text.Json;
using TypeWhisper.Cli;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class DiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-cli-discovery-" + Guid.NewGuid().ToString("N"));
    private string Profile(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    // Process 4242 stands for the running app; the resolver asks the injected check whether it is still alive.
    private const int LivePid = 4242;
    private void Discovery(string name, int port, bool? requires = null, int pid = LivePid) => File.WriteAllText(Path.Join(Profile(name), "api-discovery.json"),
        JsonSerializer.Serialize(new { version = 1, port, token = "synthetic-discovery-token", requires_authentication = requires, pid }));
    private CliConnection Resolve(CliConnectionOptions options) =>
        CliConnectionResolver.Resolve(options with { ApplicationDataRoot = _root, IsServerProcess = (pid, _) => pid == LivePid });

    [Fact]
    public void PrefersWinUiProfileAndDiscoversRequiredToken()
    {
        Discovery("TypeWhisper", 8978); Discovery("TypeWhisper-WinUI-DevUserData", 18980);
        Discovery("TypeWhisper-WinUI", 18979, true);
        var value = Resolve(new());
        Assert.Equal(18979, value.Port); Assert.Equal("synthetic-discovery-token", value.ApiToken);
    }
    [Fact]
    public void IgnoresRemovedHostAndDiscoversWinUiDevelopment()
    {
        Discovery("TypeWhisper", 18981, false);
        Discovery("TypeWhisper-WinUI-DevUserData", 18982, true);
        var result = Resolve(new());
        Assert.Equal(18982, result.Port);
        Assert.Equal("synthetic-discovery-token", result.ApiToken);
    }

    [Fact]
    public void RemovedHostDiscoveryNeverSuppliesItsPortOrCredentials()
    {
        Discovery("TypeWhisper", 18981, true);
        Assert.Throws<CliServerNotFoundException>(() => Resolve(new()));
        var explicitPort = Resolve(new(PortOverride: 18981));
        Assert.Equal(18981, explicitPort.Port);
        Assert.Null(explicitPort.ApiToken);
    }

    [Fact]
    public void ExplicitProfileIsIsolatedAndOptionalAuthenticationOmitsToken()
    {
        Discovery("fixture", 18980, false); Discovery("TypeWhisper", 8978, true);
        var value = Resolve(new(ProfileDirectory: Profile("fixture")));
        Assert.Equal(18980, value.Port); Assert.Null(value.ApiToken);
    }
    [Fact]
    public void PortOverrideDoesNotSendAnotherServersToken()
    {
        Discovery("TypeWhisper-WinUI-DevUserData", 18979, true);
        var value = Resolve(new(PortOverride: 18980));
        Assert.Equal(18980, value.Port); Assert.Null(value.ApiToken);
    }
    [Fact]
    public void MatchingPortSelectsMatchingProfileToken()
    {
        Discovery("TypeWhisper-WinUI-DevUserData", 18979, false); Discovery("TypeWhisper-WinUI", 8978, true);
        var value = Resolve(new(PortOverride: 8978));
        Assert.Equal("synthetic-discovery-token", value.ApiToken);
    }
    [Fact]
    public void ExplicitTokenOverridesEnvironmentAndDiscovery()
    {
        Discovery("TypeWhisper-WinUI", 8978, false);
        Assert.Equal("explicit", Resolve(new(ApiTokenOverride: "explicit", EnvironmentApiToken: "env")).ApiToken);
        Assert.Equal("env", Resolve(new(EnvironmentApiToken: "env")).ApiToken);
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":1,\"port\":70000,\"token\":\"secret\",\"pid\":4242}")]
    [InlineData("{\"version\":2,\"port\":18979,\"token\":\"secret\",\"pid\":4242}")]
    [InlineData("{\"version\":1,\"port\":18979,\"token\":\"secret\"}")]
    public void InvalidDiscoveryOrLeftoverPortFileFindsNoServer(string json)
    {
        var profile = Profile("TypeWhisper-WinUI"); File.WriteAllText(Path.Combine(profile, "api-discovery.json"), json);
        File.WriteAllText(Path.Combine(profile, "api-port"), "8979");
        Assert.Throws<CliServerNotFoundException>(() => Resolve(new()));
        // An explicit port is used as given, without the file's token.
        var value = Resolve(new(PortOverride: 18979));
        Assert.Equal(18979, value.Port); Assert.Null(value.ApiToken);
    }

    [Fact]
    public void DiscoveryLeftBehindByAnExitedAppSendsNothing()
    {
        Discovery("TypeWhisper-WinUI", 18979, true, pid: 777);
        Assert.Throws<CliServerNotFoundException>(() => Resolve(new()));
        // Even on its port, the stale token is not sent; a token the user supplies is.
        Assert.Null(Resolve(new(PortOverride: 18979)).ApiToken);
        Assert.Equal("env", Resolve(new(PortOverride: 18979, EnvironmentApiToken: "env")).ApiToken);
        // A live development profile is used instead of the stale production one.
        Discovery("TypeWhisper-WinUI-DevUserData", 18980, true);
        Assert.Equal(18980, Resolve(new()).Port);
    }

    [Fact]
    public void LiveCheckReceivesTheFileWriteTime()
    {
        Discovery("TypeWhisper-WinUI", 18979, true);
        var written = File.GetLastWriteTimeUtc(Path.Join(_root, "TypeWhisper-WinUI", "api-discovery.json"));
        DateTime? seen = null;
        CliConnectionResolver.Resolve(new(ApplicationDataRoot: _root, IsServerProcess: (_, time) => { seen = time; return true; }));
        Assert.Equal(written, seen);
    }

    [Fact]
    public void ProcessCheckRejectsOtherProgramsAndMissingProcesses()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        // The test runner is not TypeWhisper.
        Assert.False(CliServerProcess.IsTypeWhisperOfCurrentUser(self.Id, DateTime.UtcNow));
        Assert.False(CliServerProcess.IsTypeWhisperOfCurrentUser(int.MaxValue, DateTime.UtcNow));
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
        Discovery("TypeWhisper-WinUI", 18981, true);
        Assert.Throws<CliServerNotFoundException>(() => Resolve(new(DevMode: true)));
        Discovery("TypeWhisper-WinUI-DevUserData", 18982, true);
        var dev = Resolve(new(DevMode: true));
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

public sealed class TerminalWriterTests
{
    [Fact]
    public void ControlCharactersAreReplacedButLayoutCharactersPass()
    {
        var esc = (char)27;
        var text = "a" + esc + "]0;title" + (char)7 + "b" + (char)0x9B + "c\td\r\ne" + (char)0x7F;
        Assert.Equal("a�]0;title�b�c\td\r\ne�", CliTerminalWriter.Clean(text));
        Assert.Same("plain text äöü", CliTerminalWriter.Clean("plain text äöü"));

        using var inner = new StringWriter();
        using var writer = new CliTerminalWriter(inner);
        writer.WriteLine("x" + esc + "[2J");
        writer.Write(esc);
        Assert.Equal("x�[2J" + inner.NewLine + "�", inner.ToString());
    }
}
