using System.Text;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.AuthenticatedCli;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CliDiscoveryAndModelsTests
{
    [Fact]
    public void Discovery_ResolvesInstallerDirectoryLinksAndDeduplicatesTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "TypeWhisper-cli-links-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "version", "bin");
        var link = Path.Combine(root, "current");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "codex.exe"), "fixture");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var command = System.Diagnostics.Process.Start(start)!;
            command.WaitForExit();
            Assert.Equal(0, command.ExitCode);
            var discovery = new CliExecutableDiscovery(t => t == EnvironmentVariableTarget.Process ? link + Path.PathSeparator + target : null);
            Assert.Equal(Path.Combine(target, "codex.exe"), Assert.Single(discovery.FindCandidates("codex.exe")));
            Assert.False(CliExecutableDiscovery.IsSafeNativeExecutable(Path.Combine(link, "codex.exe"), "codex.exe"));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Discovery_FindsNpmNativeBinaryWithoutExecutingTheShim()
    {
        var root = Path.Combine(Path.GetTempPath(), "TypeWhisper-cli-npm-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "node_modules", "opencode-ai", "bin");
        Directory.CreateDirectory(bin);
        try
        {
            File.WriteAllText(Path.Combine(root, "opencode.cmd"), "fixture must never execute");
            File.WriteAllText(Path.Combine(bin, "opencode.exe"), "fixture");
            var discovery = new CliExecutableDiscovery(t => t == EnvironmentVariableTarget.Process ? root : null);
            Assert.Equal(Path.Combine(bin, "opencode.exe"), Assert.Single(discovery.FindCandidates("opencode.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("relative/codex.exe")]
    [InlineData("\\\\server\\share\\codex.exe")]
    [InlineData("\\\\?\\C:\\codex.exe")]
    public void Discovery_DoesNotResolveRemoteOrRelativePaths(string path) =>
        Assert.Null(CliExecutableDiscovery.ResolveNativeExecutable(path, "codex.exe"));

    [Fact]
    public async Task CodexCatalog_HandlesPaginationAndIgnoresHiddenAndInvalidModels()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"id":1,"result":{}}
            {"method":"notice","params":{}}
            {"id":2,"result":{"data":[{"model":"model-a","displayName":"Model A","isDefault":true},{"model":"hidden","hidden":true},{"model":"--bad"}],"nextCursor":"page-2"}}
            {"id":3,"result":{"data":[{"model":"model-b"},{"model":"model-a"}],"nextCursor":null}}

            """));
        var models = await CodexModelCatalogLoader.ExchangeAsync(input, output, default);
        Assert.Equal(["model-a", "model-b"], models.Select(m => m.Id));
        Assert.True(models[0].IsRecommended);
        var requests = Encoding.UTF8.GetString(input.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(requests, r => r.Contains("thread/start") || r.Contains("turn/start"));
        Assert.Contains(requests, r => r.Contains("page-2"));
    }

    [Theory]
    [InlineData("{\"id\":2,\"error\":{\"code\":-1}}")]
    [InlineData("{\"id\":2,\"result\":{\"data\":null}}")]
    public async Task CodexCatalog_RejectsFailedOrInvalidResponses(string response)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{}}\n" + response + "\n"));
        await Assert.ThrowsAsync<IOException>(() => CodexModelCatalogLoader.ExchangeAsync(input, output, default));
    }

    [Theory]
    [InlineData(CliProviderKind.Codex, "model-a")]
    [InlineData(CliProviderKind.Claude, "sonnet")]
    internal void ModelSelection_IsPassedAsASeparateArgument(CliProviderKind provider, string model)
    {
        var descriptor = CliProviderDescriptor.All.Single(d => d.Kind == provider);
        var arguments = descriptor.CreateInvocationArguments("directory", "schema", model).ToList();
        Assert.Equal(model, arguments[arguments.IndexOf("--model") + 1]);
        Assert.DoesNotContain("--model", descriptor.CreateInvocationArguments("directory", "schema"));
    }

    [Fact]
    public async Task ModelSettings_PersistSelectionAndKeepPreviousValueOnWriteFailure()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.GetSetting<List<PluginModelInfo>>("codexModels.v1")).Returns([new("model-a", "Model A"), new("model-b", "Model B")]);
        using var plugin = new AuthenticatedCliPlugin(new CliExecutableDiscovery(_ => null), new CliProcessRunner());
        await plugin.ActivateAsync(host.Object);
        try
        {
            await plugin.SaveTextSettingAsync("codex/codex/model", "model-a", default);
            Assert.Equal("model-a", plugin.TextSettings.Single(f => f.Id == "codex/codex/model").Value);
            host.Setup(h => h.SetSetting(AuthenticatedCliPlugin.ProfilesSetting, It.IsAny<List<CliProfile>>())).Throws<IOException>();
            await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync("codex/codex/model", "model-b", default));
            Assert.Equal("model-a", plugin.TextSettings.Single(f => f.Id == "codex/codex/model").Value);
        }
        finally { await plugin.DeactivateAsync(); }
    }
}
