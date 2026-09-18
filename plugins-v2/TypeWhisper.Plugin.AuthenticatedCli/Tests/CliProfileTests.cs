using Moq;
using PortableMigration.Tests;
using System.Text.Json;
using TypeWhisper.Plugin.AuthenticatedCli;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CliProfileTests
{
    [Fact]
    public async Task ProfilesKeepStableWorkflowIdentitiesAndSeparateModelsAcrossRestart()
    {
        using var fixture = new PortableFixture();
        using var plugin = CreatePlugin();
        await plugin.ActivateAsync(fixture.Host);
        try
        {
            var original = Ids(plugin);
            await plugin.ExecuteSettingsActionAsync("add", default);
            var id = plugin.ConnectionIdentity;
            await plugin.SaveProfileSettingsAsync(id, new Dictionary<string, string>
            {
                [id + "/name"] = "Claude editing", [id + "/provider"] = "claude", [id + "/claude/model"] = "sonnet"
            }, null, default);
            Assert.Equal(original.Append("authenticated-cli-" + id), Ids(plugin));
            Assert.Equal("Claude editing", plugin.AdditionalLlmProviders.Last().ProviderName);
            await plugin.DeactivateAsync();
            using var restarted = CreatePlugin();
            await restarted.ActivateAsync(fixture.Host);
            try
            {
                await restarted.SaveTextSettingAsync("profile", id, default);
                Assert.Equal("sonnet", restarted.TextSettings.Single(f => f.Id == id + "/claude/model").Value);
                Assert.Equal(original.Append("authenticated-cli-" + id), Ids(restarted));
                await restarted.ExecuteSettingsActionAsync(id + "/delete", default);
                Assert.Equal(original, Ids(restarted));
                await Assert.ThrowsAsync<ArgumentException>(() => restarted.SaveProfileSettingsAsync(id, new Dictionary<string, string>(), null, default));
            }
            finally { await restarted.DeactivateAsync(); }
        }
        finally { await plugin.DeactivateAsync(); }
    }

    [Fact]
    public async Task FailedProfileWritePreservesNameModelAndEnvironmentTogether()
    {
        var host = new Mock<IPluginHostServices>();
        using var plugin = CreatePlugin();
        await plugin.ActivateAsync(host.Object);
        try
        {
            host.Setup(h => h.SetSetting(AuthenticatedCliPlugin.ProfilesSetting, It.IsAny<List<CliProfile>>())).Throws<IOException>();
            await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync("codex", new Dictionary<string, string>
            {
                ["codex/name"] = "Changed", ["codex/environment"] = "CODEX_HOME=" + Path.GetTempPath()
            }, null, default));
            Assert.Equal("Codex CLI", plugin.TextSettings.Single(f => f.Id == "codex/name").Value);
            Assert.Equal("", plugin.TextSettings.Single(f => f.Id == "codex/environment").Value);
            await Assert.ThrowsAsync<IOException>(() => plugin.ExecuteSettingsActionAsync("add", default));
            Assert.Equal(4, plugin.AdditionalLlmProviders.Count);
        }
        finally { await plugin.DeactivateAsync(); }
    }

    [Theory]
    [InlineData("PATH=C:\\")]
    [InlineData("CODEX_HOME=relative")]
    [InlineData("OPENAI_API_KEY=secret")]
    [InlineData("CODEX_HOME=C:\\\nCODEX_HOME=C:\\")]
    public async Task InvalidEnvironmentNeverChangesSavedProfile(string environment)
    {
        using var plugin = CreatePlugin();
        await plugin.ActivateAsync(new Mock<IPluginHostServices>().Object);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("codex", new Dictionary<string, string>
            { ["codex/environment"] = environment }, null, default));
            Assert.Equal("", plugin.TextSettings.Single(f => f.Id == "codex/environment").Value);
        }
        finally { await plugin.DeactivateAsync(); }
    }

    [Fact]
    public async Task DifferentCodexProfilesPassTheirOwnSessionDirectoryToProbesAndRequests()
    {
        using var fixture = new PortableFixture();
        var executable = Path.Combine(fixture.Root, "codex.exe");
        File.WriteAllText(executable, "fixture");
        var runner = new CaptureRunner();
        using var plugin = new AuthenticatedCliPlugin(new CliExecutableDiscovery(_ => null), runner);
        await plugin.ActivateAsync(fixture.Host);
        try
        {
            foreach (var name in new[] { "first", "second" })
            {
                var directory = Path.Combine(fixture.Root, name);
                Directory.CreateDirectory(directory);
                await plugin.ExecuteSettingsActionAsync("add", default);
                var id = plugin.ConnectionIdentity;
                var count = runner.Requests.Count;
                await plugin.SaveProfileSettingsAsync(id, new Dictionary<string, string>
                {
                    [id + "/name"] = name, [id + "/codex/executable"] = executable,
                    [id + "/environment"] = "CODEX_HOME=" + directory
                }, null, default);
                var role = plugin.AdditionalLlmProviders.Last();
                Assert.True(role.IsAvailable);
                Assert.Equal("processed", await role.ProcessAsync("Fix spelling", "fixture", "default", default));
                var requests = runner.Requests.Skip(count).ToArray();
                Assert.True(requests.Length >= 4);
                Assert.All(requests, r => Assert.Equal(directory, r.EnvironmentOverrides!["CODEX_HOME"]));
                Assert.Equal("", plugin.TextSettings.Single(f => f.Id == id + "/claude/executable").Value);
            }
        }
        finally { await plugin.DeactivateAsync(); }
    }

    [Fact]
    public async Task DraftModelRefreshUsesEnteredSessionAndOnlyCommitsOnSave()
    {
        using var fixture = new PortableFixture();
        var executable = Path.Combine(fixture.Root, "codex.exe");
        File.WriteAllText(executable, "fixture");
        var session = Path.Combine(fixture.Root, "session");
        Directory.CreateDirectory(session);
        var runner = new CaptureRunner();
        var fail = false;
        using var plugin = new AuthenticatedCliPlugin(new CliExecutableDiscovery(_ => null), runner,
            (path, directory, ct, environment) =>
            {
                Assert.Equal(executable, path);
                Assert.Equal(session, environment!["CODEX_HOME"]);
                if (fail) throw new IOException("Fixture catalog failure");
                return Task.FromResult<IReadOnlyList<TypeWhisper.PluginSDK.Models.PluginModelInfo>>([new("fixture-model", "Fixture model")]);
            });
        await plugin.ActivateAsync(fixture.Host);
        try
        {
            var values = new Dictionary<string, string>
            {
                ["codex/codex/executable"] = executable,
                ["codex/environment"] = "CODEX_HOME=" + session
            };
            var result = await plugin.ExecuteProfileActionAsync("codex", "codex/refresh", values, null, default);
            Assert.True(result.HasPendingChanges);
            Assert.Equal("", plugin.TextSettings.Single(f => f.Id == "codex/environment").Value);
            Assert.DoesNotContain(plugin.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "fixture-model");
            Assert.Contains(plugin.TextSettings.Single(f => f.Id == "codex/codex/model").Choices, m => m.Value == "fixture-model");
            fail = true;
            await Assert.ThrowsAsync<IOException>(() => plugin.ExecuteProfileActionAsync("codex", "codex/refresh", values, null, default));
            values["codex/codex/model"] = "fixture-model";
            await plugin.SaveProfileSettingsAsync("codex", values, null, default);
            Assert.Contains(plugin.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "fixture-model");
            Assert.Equal("CODEX_HOME=" + session, plugin.TextSettings.Single(f => f.Id == "codex/environment").Value);
            await plugin.ExecuteSettingsActionAsync("add", default);
            await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("codex", values, null, default));
        }
        finally { await plugin.DeactivateAsync(); }
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("executable")]
    public async Task OpenCodeCatalogsRemainBoundToTheirProfileAndConnectionAcrossRestart(string changedConnection)
    {
        using var fixture = new PortableFixture();
        var executable = Path.Combine(fixture.Root, "opencode.exe");
        File.WriteAllText(executable, "fixture");
        var profiles = new List<CliProfile>();
        foreach (var id in new[] { "first", "second" })
        {
            var directory = Path.Combine(fixture.Root, id); Directory.CreateDirectory(directory);
            profiles.Add(new CliProfile { Id = id, Name = id, Provider = "opencode", Executable = executable,
                Environment = new() { ["XDG_DATA_HOME"] = directory } });
        }
        fixture.Host.SetSetting(AuthenticatedCliPlugin.ProfilesSetting, profiles);
        var runner = new ProfileOpenCodeRunner();
        using (var plugin = new AuthenticatedCliPlugin(new CliExecutableDiscovery(_ => null), runner))
        {
            await plugin.ActivateAsync(fixture.Host);
            try
            {
                await plugin.RefreshFromSettingsAsync();
                Assert.True(plugin.AdditionalLlmProviders[0].IsAvailable);
                Assert.Equal("opencode/first-free", Assert.Single(plugin.AdditionalLlmProviders[0].SupportedModels).Id);
                Assert.False(plugin.AdditionalLlmProviders[1].IsAvailable);
                Assert.Empty(plugin.AdditionalLlmProviders[1].SupportedModels);
                await Assert.ThrowsAsync<PluginRequestException>(() => plugin.AdditionalLlmProviders[1]
                    .ProcessAsync("", "fixture", "opencode/first-free", default));
                runner.SecondAvailable = true;
                await plugin.RefreshFromSettingsAsync();
                Assert.Equal("opencode/second-free", Assert.Single(plugin.AdditionalLlmProviders[1].SupportedModels).Id);
                Assert.Equal("opencode/first-free", Assert.Single(plugin.AdditionalLlmProviders[0].SupportedModels).Id);
            }
            finally { await plugin.DeactivateAsync(); }
        }
        runner.FailAllCatalogs = true;
        using var restarted = new AuthenticatedCliPlugin(new CliExecutableDiscovery(_ => null), runner);
        await restarted.ActivateAsync(fixture.Host);
        try
        {
            await restarted.RefreshFromSettingsAsync();
            Assert.All(restarted.AdditionalLlmProviders, p => Assert.True(p.IsAvailable));
            Assert.Equal("opencode/first-free", Assert.Single(restarted.AdditionalLlmProviders[0].SupportedModels).Id);
            Assert.Equal("opencode/second-free", Assert.Single(restarted.AdditionalLlmProviders[1].SupportedModels).Id);
            var changedDirectory = Path.Combine(fixture.Root, "changed"); Directory.CreateDirectory(changedDirectory);
            var changedExecutable = Path.Combine(changedDirectory, "opencode.exe"); File.WriteAllText(changedExecutable, "fixture");
            var changes = changedConnection == "environment"
                ? new Dictionary<string, string> { ["first/environment"] = "XDG_DATA_HOME=" + changedDirectory }
                : new Dictionary<string, string> { ["first/opencode/executable"] = changedExecutable };
            await restarted.SaveProfileSettingsAsync("first", changes, null, default);
            Assert.False(restarted.AdditionalLlmProviders[0].IsAvailable);
            Assert.Empty(restarted.AdditionalLlmProviders[0].SupportedModels);
            Assert.True(restarted.AdditionalLlmProviders[1].IsAvailable);
            Assert.Equal("opencode/second-free", Assert.Single(restarted.AdditionalLlmProviders[1].SupportedModels).Id);
        }
        finally { await restarted.DeactivateAsync(); }
    }

    private sealed class ProfileOpenCodeRunner : ICliProcessRunner
    {
        internal bool SecondAvailable { get; set; }
        internal bool FailAllCatalogs { get; set; }
        public Task<CliProcessResult> RunAsync(CliProcessRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(request.EnvironmentOverrides!["XDG_DATA_HOME"]);
            var catalog = request.Arguments[0] == "models";
            if (catalog && (FailAllCatalogs || id == "second" && !SecondAvailable))
                return Task.FromResult(new CliProcessResult(1, "", "fixture failure", TimeSpan.Zero, 0, 15));
            var output = request.Arguments.Contains("--version") ? "opencode 1.3.0"
                : request.Arguments.Contains("--help") ? string.Join(" ", CliProviderDescriptor.All.Single(d => d.Kind == CliProviderKind.OpenCode).RequiredHelpTokens)
                : request.Arguments[0] == "auth" ? "OpenCode Zen"
                : "opencode/" + id + "-free\n" + JsonSerializer.Serialize(new {
                    id = id + "-free", providerID = "opencode", name = id,
                    status = "active", modalities = new { input = new[] { "text" }, output = new[] { "text" } },
                    cost = new { input = 0, output = 0 }, variants = new { } });
            return Task.FromResult(new CliProcessResult(0, output, "", TimeSpan.Zero, output.Length, 0));
        }
    }

    private static AuthenticatedCliPlugin CreatePlugin() => new(new CliExecutableDiscovery(_ => null), new CaptureRunner());
    private static string[] Ids(AuthenticatedCliPlugin plugin) => plugin.AdditionalLlmProviders
        .Select(p => ((ILlmProviderSelectionIdentity)p).LlmSelectionId).ToArray();

    private sealed class CaptureRunner : ICliProcessRunner
    {
        internal List<CliProcessRequest> Requests { get; } = [];
        public Task<CliProcessResult> RunAsync(CliProcessRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            var output = request.Arguments.Contains("--version") ? "codex 0.155.0"
                : request.Arguments.Contains("--help") ? string.Join(" ", CliProviderDescriptor.All[0].RequiredHelpTokens)
                : request.Arguments.Contains("login") ? "Logged in using subscription"
                : "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"text\\\":\\\"processed\\\"}\"}}\n{\"type\":\"turn.completed\"}";
            return Task.FromResult(new CliProcessResult(0, output, "", TimeSpan.Zero, output.Length, 0));
        }
    }
}
