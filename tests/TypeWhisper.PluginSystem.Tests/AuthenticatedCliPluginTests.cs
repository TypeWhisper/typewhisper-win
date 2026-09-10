using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.AuthenticatedCli;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AuthenticatedCliPluginTests
{
    [Fact]
    public async Task CodexProvider_UsesStructuredStdinAndFixedArguments()
    {
        using var fake = FakeCliInstallation.Create("success", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        var role = GetRole(plugin, "authenticated-cli-codex");
        const string instruction = "Transform the text and return only JSON.";
        const string input = "\"; $(touch marker) & whoami | echo %TOKEN% `cmd`\r\n今天天气很好 --model evil";

        var result = await role.ProcessAsync(instruction, input, "default", CancellationToken.None);

        Assert.Equal("processed", result);
        Assert.True(role.IsAvailable);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        var root = capture.RootElement;
        using var envelope = JsonDocument.Parse(root.GetProperty("standardInput").GetString()!);
        Assert.Equal("typewhisper.prompt-processing.v1", envelope.RootElement.GetProperty("protocol").GetString());
        Assert.Equal(instruction, envelope.RootElement.GetProperty("instruction").GetString());
        Assert.Equal(input, envelope.RootElement.GetProperty("input").GetString());
        Assert.DoesNotContain(
            root.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()),
            argument => argument is not null && argument.Contains(input, StringComparison.Ordinal));
        Assert.Contains("--ignore-user-config", root.GetProperty("arguments").ToString(), StringComparison.Ordinal);
        Assert.Contains("--strict-config", root.GetProperty("arguments").ToString(), StringComparison.Ordinal);
        Assert.Contains("features.shell_tool=false", root.GetProperty("arguments").ToString(), StringComparison.Ordinal);
        Assert.Contains("features.apps=false", root.GetProperty("arguments").ToString(), StringComparison.Ordinal);
        Assert.Contains("apps._default.enabled=false", root.GetProperty("arguments").ToString(), StringComparison.Ordinal);
        Assert.Contains("agents.enabled=false", root.GetProperty("arguments").ToString(), StringComparison.Ordinal);
        Assert.Contains("project_doc_max_bytes=0", root.GetProperty("arguments").ToString(), StringComparison.Ordinal);
        var workingDirectory = root.GetProperty("workingDirectory").GetString()!;
        Assert.DoesNotContain(RepositoryRoot(), workingDirectory, StringComparison.OrdinalIgnoreCase);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ClaudeProvider_UsesSafeModeAndStructuredOutput()
    {
        using var fake = FakeCliInstallation.Create("success", "claude.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-claude");

        var result = await role.ProcessAsync("Instruction", "Input", "default", CancellationToken.None);

        Assert.Equal("processed", result);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        var arguments = capture.RootElement.GetProperty("arguments").ToString();
        Assert.Contains("--safe-mode", arguments, StringComparison.Ordinal);
        Assert.Contains("--strict-mcp-config", arguments, StringComparison.Ordinal);
        Assert.Contains("--no-session-persistence", arguments, StringComparison.Ordinal);
        Assert.Contains("--disallowedTools", arguments, StringComparison.Ordinal);
        Assert.Contains("--system-prompt", arguments, StringComparison.Ordinal);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task AntigravityProvider_RemainsUnavailableWhenBinarySupportsStructuredOutput()
    {
        using var fake = FakeCliInstallation.Create("success", "agy.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        var descriptor = Descriptor(CliProviderKind.Antigravity);

        Assert.Equal(CliAvailabilityState.SafetyControlsUnavailable, plugin.GetSnapshot(descriptor).State);
        Assert.False(GetRole(plugin, descriptor.SelectionId).IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task SignedOutProvider_IsNotAdvertisedAsAvailable()
    {
        using var fake = FakeCliInstallation.Create("signed-out", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(
            CliAvailabilityState.SignedOut,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State);
        Assert.False(GetRole(plugin, "authenticated-cli-codex").IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ClaudeAuthenticationProbe_RequiresPositiveLoggedInField()
    {
        using var fake = FakeCliInstallation.Create("auth-unknown", "claude.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(
            CliAvailabilityState.AuthenticationUnknown,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Claude)).State);
        Assert.False(GetRole(plugin, "authenticated-cli-claude").IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RuntimeAuthenticationFailure_DisablesProviderAndIsActionable()
    {
        using var fake = FakeCliInstallation.Create("auth-error", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.False(role.IsAvailable);
        Assert.Equal(
            CliAvailabilityState.SignedOut,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RuntimeRateLimit_RemainsTransientWithoutDisablingProvider()
    {
        using var fake = FakeCliInstallation.Create("rate-limit", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.RateLimit, error.FailureKind);
        Assert.True(error.IsTransient);
        Assert.True(role.IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task NetworkFailureMentioningAuthenticationAndModel_RemainsTransient()
    {
        using var fake = FakeCliInstallation.Create("network-auth-model", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "default",
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Network, error.FailureKind);
        Assert.True(error.IsTransient);
        Assert.True(role.IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task UnsupportedModel_FailsBeforeLaunchingProviderCli()
    {
        using var fake = FakeCliInstallation.Create("success", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        var role = GetRole(plugin, "authenticated-cli-codex");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            "unsupported-model",
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.InvalidRequest, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.False(File.Exists(fake.CapturePath));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task PreviouslySelectedExecutable_DoesNotSilentlySwitchAfterPathChange()
    {
        using var fake = FakeCliInstallation.Create("success", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        host.Setup(service => service.GetSetting<string>("selectedExecutable.codex"))
            .Returns(Path.Combine(fake.DirectoryPath, "previous", "codex.exe"));

        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();

        var snapshot = plugin.GetSnapshot(Descriptor(CliProviderKind.Codex));
        Assert.Equal(CliAvailabilityState.SelectedExecutableMissing, snapshot.State);
        Assert.False(GetRole(plugin, "authenticated-cli-codex").IsAvailable);
        Assert.Equal(fake.ExecutablePath("codex.exe"), Assert.Single(snapshot.Candidates));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task MissingCli_BecomesReadyAfterRefreshWithoutRestart()
    {
        using var fake = FakeCliInstallation.CreateEmpty("refresh");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();
        Assert.Equal(
            CliAvailabilityState.MissingExecutable,
            plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State);

        fake.Install("codex.exe");
        await plugin.RefreshFromSettingsAsync();

        Assert.Equal(CliAvailabilityState.Ready, plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State);
        host.Verify(service => service.NotifyCapabilitiesChanged(), Times.AtLeastOnce);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void Discovery_RefreshesPathSourcesAndRejectsScripts()
    {
        using var fake = FakeCliInstallation.CreateEmpty("discovery");
        string? processPath = null;
        var discovery = new CliExecutableDiscovery(target =>
            target == EnvironmentVariableTarget.Process ? processPath : null);

        Assert.Empty(discovery.FindCandidates("codex.exe"));
        fake.Install("codex.exe");
        processPath = $"\"{fake.DirectoryPath}\"{Path.PathSeparator}.";

        Assert.Equal(fake.ExecutablePath("codex.exe"), Assert.Single(discovery.FindCandidates("codex.exe")));
        var scriptPath = Path.Combine(fake.DirectoryPath, "codex.cmd");
        File.WriteAllText(scriptPath, "exit /b 0");
        Assert.False(CliExecutableDiscovery.IsSafeNativeExecutable(scriptPath, "codex.exe"));
        Assert.False(CliExecutableDiscovery.IsSafeNativeExecutable("codex.exe", "codex.exe"));
        Assert.False(CliPathSafety.IsSafeLocalDirectory($"\\\\?\\{fake.DirectoryPath}"));
    }

    [Fact]
    public void CapabilityProbe_RequiresExactSafetyFlagTokens()
    {
        var codex = Descriptor(CliProviderKind.Codex);
        const string exact = "--ignore-user-config --ignore-rules --ephemeral --output-schema --strict-config --json --sandbox --skip-git-repo-check";
        const string substringOnly = "--ignore-user-config --ignore-rules --ephemeral --output-schema --strict-config --json-schema --sandbox --skip-git-repo-check";

        Assert.True(codex.HasRequiredCapabilities(exact));
        Assert.False(codex.HasRequiredCapabilities(substringOnly));
    }

    [Fact]
    public async Task ThrowingSettingsActivitySubscriber_DoesNotLeakRefreshGate()
    {
        using var fake = FakeCliInstallation.Create("success", "codex.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        plugin.SettingsActivityChanged += _ => throw new InvalidOperationException("subscriber failure");
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await plugin.RefreshFromSettingsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CliAvailabilityState.Ready, plugin.GetSnapshot(Descriptor(CliProviderKind.Codex)).State);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task MultipleInstallations_RequireExplicitSelection()
    {
        using var first = FakeCliInstallation.Create("success", "codex.exe");
        using var second = FakeCliInstallation.Create("success", "codex.exe");
        using var plugin = CreatePlugin(
            first.DirectoryPath + Path.PathSeparator + second.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();

        var snapshot = plugin.GetSnapshot(Descriptor(CliProviderKind.Codex));
        Assert.Equal(CliAvailabilityState.AmbiguousExecutable, snapshot.State);
        Assert.Equal(2, snapshot.Candidates.Count);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void ProcessStartInfo_UsesNoShellAndDropsUnrelatedSecrets()
    {
        using var fake = FakeCliInstallation.Create("success", "codex.exe");
        var request = CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5));
        var previous = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_SECRET");
        Environment.SetEnvironmentVariable("TYPEWHISPER_TEST_SECRET", "must-not-leak");
        try
        {
            var startInfo = CliProcessRunner.CreateStartInfo(request);

            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.True(startInfo.CreateNoWindow);
            Assert.Equal(fake.ExecutablePath("codex.exe"), startInfo.FileName);
            Assert.False(startInfo.Environment.ContainsKey("TYPEWHISPER_TEST_SECRET"));
            Assert.False(startInfo.Environment.ContainsKey("OPENAI_API_KEY"));
            Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.False(startInfo.Environment.ContainsKey("ComSpec"));
            Assert.Equal(".EXE", startInfo.Environment["PATHEXT"]);
            Assert.Equal(fake.WorkingDirectory, startInfo.WorkingDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TYPEWHISPER_TEST_SECRET", previous);
        }
    }

    [Fact]
    public async Task Runner_DrainsLargeStderrWithoutDeadlock()
    {
        using var fake = FakeCliInstallation.Create("stderr", "codex.exe");
        var result = await new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(48 * 1024, result.StandardErrorBytes);
        Assert.Equal("processed", Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(result.StandardOutput));
    }

    [Fact]
    public async Task Runner_RejectsInvalidJsonAfterSuccessfulExit()
    {
        using var fake = FakeCliInstallation.Create("invalid-json", "codex.exe");
        var result = await new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.ThrowsAny<JsonException>(() =>
            Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(result.StandardOutput));
    }

    [Fact]
    public async Task Runner_RejectsInvalidUtf8AsMalformedOutput()
    {
        using var fake = FakeCliInstallation.Create("invalid-utf8", "codex.exe");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5)),
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Unknown, error.FailureKind);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Runner_ReportsCrashWithoutTreatingOutputAsSuccess()
    {
        using var fake = FakeCliInstallation.Create("crash", "codex.exe");
        var result = await new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        Assert.Equal(42, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
    }

    [Fact]
    public async Task Runner_KillsTimedOutProcess()
    {
        using var fake = FakeCliInstallation.Create("timeout", "codex.exe");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromMilliseconds(300)),
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Timeout, error.FailureKind);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Runner_PreservesUserCancellation()
    {
        using var fake = FakeCliInstallation.Create("timeout", "codex.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(10)),
            cancellation.Token));
    }

    [Fact]
    public async Task Runner_RejectsOversizedOutputAndCleansUp()
    {
        using var fake = FakeCliInstallation.Create("huge-output", "codex.exe");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5)),
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Unknown, error.FailureKind);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Runner_KillsChildProcessOnTimeout()
    {
        using var fake = FakeCliInstallation.Create("child", "codex.exe");

        _ = await Assert.ThrowsAsync<PluginRequestException>(() => new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(2)),
            CancellationToken.None));

        var childPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(fake.WorkingDirectory, "spawned.pid")));
        Assert.True(await WaitForProcessExitAsync(childPid, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Runner_ContainsChildSpawnedBeforeParentExits()
    {
        using var fake = FakeCliInstallation.Create("instant-child", "codex.exe");

        var result = await new CliProcessRunner().RunAsync(
            CreateRunnerRequest(fake, "input", TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var childPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(fake.WorkingDirectory, "spawned.pid")));
        Assert.True(await WaitForProcessExitAsync(childPid, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Runner_NeverInterpretsInjectionStringsAsArguments()
    {
        using var fake = FakeCliInstallation.Create("success", "codex.exe");
        const string injection = "\" & echo pwned > marker | $(whoami) `cmd` %PATH% !PATH! ; --help";
        var request = CreateRunnerRequest(fake, injection, TimeSpan.FromSeconds(5));

        var result = await new CliProcessRunner().RunAsync(request, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(request.Arguments, argument => argument.Contains(injection, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fake.WorkingDirectory, "marker")));
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        Assert.Equal(injection, capture.RootElement.GetProperty("standardInput").GetString());
    }

    [Fact]
    public void Parsers_RequireProviderNativeTerminalEnvelopeAndExactLogicalSchema()
    {
        Assert.Equal(
            "codex",
            Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(
                "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"text\\\":\\\"codex\\\"}\"}}\n{\"type\":\"turn.completed\"}"));
        Assert.Equal(
            "claude",
            Descriptor(CliProviderKind.Claude).ParseSuccessfulOutput(
                "{\"type\":\"result\",\"subtype\":\"success\",\"structured_output\":{\"text\":\"claude\"}}"));
        Assert.Equal(
            "agy",
            Descriptor(CliProviderKind.Antigravity).ParseSuccessfulOutput(
                "{\"type\":\"init\"}\n{\"type\":\"result\",\"structured_output\":{\"text\":\"agy\"}}"));
        Assert.Throws<CliProtocolException>(() =>
            Descriptor(CliProviderKind.Claude).ParseSuccessfulOutput(
                "{\"type\":\"result\",\"subtype\":\"success\",\"structured_output\":{\"text\":\"value\",\"extra\":true}}"));
        Assert.Throws<CliProtocolException>(() =>
            Descriptor(CliProviderKind.Codex).ParseSuccessfulOutput(
                "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"text\\\":\\\"partial\\\"}\"}}"));
    }

    [Fact]
    public void Providers_ExposeStableFourthOpenCodeRoleAndConsistentVersion()
    {
        using var plugin = new AuthenticatedCliPlugin();

        Assert.Equal(4, plugin.AdditionalLlmProviders.Count);
        Assert.Equal(
            new[]
            {
                "authenticated-cli-codex",
                "authenticated-cli-claude",
                "authenticated-cli-antigravity",
                "authenticated-cli-opencode"
            },
            plugin.AdditionalLlmProviders
                .Select(provider => ((ILlmProviderSelectionIdentity)provider).LlmSelectionId));
        Assert.Equal("1.1.0", plugin.PluginVersion);
        Assert.Equal("opencode.exe", Descriptor(CliProviderKind.OpenCode).ExecutableName);
    }

    [Fact]
    public async Task OpenCodeProvider_UsesOnlyFreeCatalogModelsAndExactIsolatedInvocation()
    {
        using var fake = FakeCliInstallation.Create("opencode-success", "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");
        var model = Assert.Single(role.SupportedModels);

        Assert.Equal("opencode/muse-spark-1.3-contributor-free", model.Id);
        Assert.Equal("Muse Spark 1.3 Free", model.DisplayName);
        Assert.True(model.IsRecommended);
        Assert.True(role.IsAvailable);
        host.Verify(service => service.SetSetting(
            AuthenticatedCliPlugin.OpenCodeCatalogSettingName,
            It.Is<OpenCodeModelCatalogCache>(cache =>
                cache.Version == 1
                && cache.Models.Count == 1
                && cache.Models[0].Id == model.Id)), Times.AtLeastOnce);

        var result = await role.ProcessAsync("Instruction", "Synthetic input", model.Id, CancellationToken.None);

        Assert.Equal("processed", result);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath));
        var root = capture.RootElement;
        Assert.Equal(
            new[]
            {
                "run", "--pure", "--format", "json", "--title", "TypeWhisper",
                "--dir", root.GetProperty("workingDirectory").GetString(),
                "--agent", "typewhisper", "--model", model.Id
            },
            root.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()));
        var arguments = root.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString()!)
            .ToList();
        Assert.DoesNotContain("--variant", arguments);
        Assert.DoesNotContain("--auto", arguments);
        Assert.DoesNotContain("--continue", arguments);
        Assert.DoesNotContain("--session", arguments);
        Assert.DoesNotContain("--share", arguments);
        Assert.DoesNotContain("--attach", arguments);

        var environment = root.GetProperty("environment");
        var workingDirectory = root.GetProperty("workingDirectory").GetString()!;
        Assert.Equal("typewhisper", environment.GetProperty("OPENCODE_CLIENT").GetString());
        Assert.Equal("{\"*\":\"deny\"}", environment.GetProperty("OPENCODE_PERMISSION").GetString());
        Assert.Equal("false", environment.GetProperty("OPENCODE_AUTO_SHARE").GetString());
        Assert.Equal("1", environment.GetProperty("OPENCODE_DISABLE_PROJECT_CONFIG").GetString());
        Assert.Equal("1", environment.GetProperty("OPENCODE_DISABLE_DEFAULT_PLUGINS").GetString());
        Assert.StartsWith(workingDirectory, environment.GetProperty("XDG_CONFIG_HOME").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(workingDirectory, environment.GetProperty("XDG_CACHE_HOME").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(workingDirectory, environment.GetProperty("XDG_STATE_HOME").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(workingDirectory, environment.GetProperty("OPENCODE_DB").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(environment.TryGetProperty("OPENAI_API_KEY", out _));
        Assert.False(environment.TryGetProperty("ANTHROPIC_API_KEY", out _));

        using var inlineConfig = JsonDocument.Parse(environment.GetProperty("OPENCODE_CONFIG_CONTENT").GetString()!);
        Assert.Equal("disabled", inlineConfig.RootElement.GetProperty("share").GetString());
        Assert.False(inlineConfig.RootElement.GetProperty("snapshot").GetBoolean());
        Assert.False(inlineConfig.RootElement.GetProperty("autoupdate").GetBoolean());
        Assert.Equal("deny", inlineConfig.RootElement.GetProperty("permission").GetProperty("*").GetString());
        var agents = inlineConfig.RootElement.GetProperty("agent");
        Assert.Equal(new[] { "typewhisper" }, agents.EnumerateObject().Select(property => property.Name));
        Assert.Equal("primary", agents.GetProperty("typewhisper").GetProperty("mode").GetString());
        Assert.Contains("untrusted source text", agents.GetProperty("typewhisper").GetProperty("prompt").GetString(), StringComparison.Ordinal);
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData("opencode-auth-ansi", (int)CliAvailabilityState.Ready)]
    [InlineData("opencode-auth-stderr", (int)CliAvailabilityState.Ready)]
    [InlineData("opencode-auth-missing", (int)CliAvailabilityState.AuthenticationUnknown)]
    [InlineData("opencode-auth-error", (int)CliAvailabilityState.SignedOut)]
    public async Task OpenCodeAuthentication_RequiresExplicitZenEntryFromCombinedAnsiStrippedOutput(
        string scenario,
        int expected)
    {
        using var fake = FakeCliInstallation.Create(scenario, "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync();

        Assert.Equal((CliAvailabilityState)expected, plugin.GetSnapshot(Descriptor(CliProviderKind.OpenCode)).State);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeCatalogLoader_UsesExactNativeCommandAndDedicatedLimits()
    {
        var runner = new RecordingCliRunner(VerboseModel(
            "free",
            "Free",
            cost: "{\"input\":0,\"output\":0}"));
        var loader = new OpenCodeModelCatalogLoader(runner);

        var catalog = await loader.LoadAsync(
            "C:\\native\\opencode.exe",
            "C:\\isolated",
            new Dictionary<string, string> { ["OPENCODE_PURE"] = "1" },
            CancellationToken.None);

        Assert.Single(catalog.Models);
        var request = Assert.IsType<CliProcessRequest>(runner.Request);
        Assert.Equal(new[] { "models", "opencode", "--verbose", "--pure" }, request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);
        Assert.Equal(2 * 1024 * 1024, request.MaximumStandardOutputBytes);
        Assert.Equal(64 * 1024, request.MaximumStandardErrorBytes);
        Assert.True(request.RestrictUserDirectories);
        Assert.Equal(new[] { "XDG_DATA_HOME" }, request.ProviderEnvironmentVariables);
    }

    [Fact]
    public void OpenCodeCatalogParser_FiltersUnsafeEntriesAndChecksEveryCostNumber()
    {
        var output = string.Join('\n', new[]
        {
            VerboseModel("first-free", "First Free", cost: "{\"input\":0,\"output\":0,\"cache\":{\"read\":0},\"tiers\":[{\"input\":0,\"output\":0}]}", variants: "{\"safe\":{},\"bad value\":{}}"),
            VerboseModel("exponent-zero", "Exponent Zero", cost: "{\"input\":0e999999,\"output\":-0.0e-999999}"),
            VerboseModel("underflow-paid", "Underflow Paid", cost: "{\"input\":1e-400,\"output\":0}"),
            VerboseModel("unknown-nested-cost", "Unknown Nested Cost", cost: "{\"input\":0,\"output\":0,\"cache\":{\"read\":\"0\"}}"),
            VerboseModel("cache-paid", "Cache Paid", cost: "{\"input\":0,\"output\":0,\"cache\":{\"read\":0.01}}"),
            VerboseModel("tier-paid", "Tier Paid", cost: "{\"input\":0,\"output\":0,\"tiers\":[{\"input\":1,\"output\":0}]}"),
            VerboseModel("direct-paid", "Direct Paid", cost: "{\"input\":1,\"output\":2}"),
            VerboseModel("deprecated", "Deprecated", cost: "{\"input\":0,\"output\":0}", status: "deprecated"),
            VerboseModel("wrong-provider", "Wrong Provider", cost: "{\"input\":0,\"output\":0}", providerId: "other"),
            VerboseModel("wrong-id", "Wrong ID", cost: "{\"input\":0,\"output\":0}", metadataId: "different"),
            VerboseModel("no-text-output", "No Text", cost: "{\"input\":0,\"output\":0}", outputModalities: "[\"image\"]"),
            VerboseModel("duplicate", "Duplicate A", cost: "{\"input\":0,\"output\":0}"),
            VerboseModel("duplicate", "Duplicate B", cost: "{\"input\":0,\"output\":0}")
        });

        var catalog = OpenCodeModelCatalogLoader.Parse(output, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            new[] { "opencode/first-free", "opencode/exponent-zero", "opencode/underflow-paid", "opencode/unknown-nested-cost", "opencode/cache-paid", "opencode/tier-paid", "opencode/direct-paid" },
            catalog.Models.Select(model => model.Id));
        Assert.True(catalog.Models[0].IsFree);
        Assert.True(catalog.Models[1].IsFree);
        Assert.Equal(new[] { "safe" }, catalog.Models[0].Variants);
        Assert.All(catalog.Models.Skip(2), model => Assert.False(model.IsFree));
    }

    [Fact]
    public void OpenCodeCatalogParser_RejectsUnparseableCatalogAndSkipsMalformedEntry()
    {
        Assert.Throws<CliProtocolException>(() => OpenCodeModelCatalogLoader.Parse(
            "opencode/broken\n{not-json",
            DateTimeOffset.UnixEpoch));

        var catalog = OpenCodeModelCatalogLoader.Parse(
            "opencode/broken\n{not-json\n" + VerboseModel(
                "valid",
                "Valid",
                cost: "{\"input\":0,\"output\":0}"),
            DateTimeOffset.UnixEpoch);
        Assert.Equal("opencode/valid", Assert.Single(catalog.Models).Id);
    }

    [Fact]
    public async Task OpenCodeWithNoFreeModels_IsUnavailableAndNeverFallsBackToDefault()
    {
        using var fake = FakeCliInstallation.Create("opencode-catalog-none", "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");

        Assert.Equal(CliAvailabilityState.NoFreeModels, plugin.GetSnapshot(Descriptor(CliProviderKind.OpenCode)).State);
        Assert.False(role.IsAvailable);
        Assert.Empty(role.SupportedModels);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeCatalogFailure_KeepsValidatedCachedFreeListVisible()
    {
        using var fake = FakeCliInstallation.Create("opencode-catalog-fail", "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        host.Setup(service => service.GetSetting<OpenCodeModelCatalogCache>(AuthenticatedCliPlugin.OpenCodeCatalogSettingName))
            .Returns(new OpenCodeModelCatalogCache(
                1,
                DateTimeOffset.UnixEpoch,
                [new OpenCodeCachedModel("opencode/cached-free", "Cached Free", [])]));
        await plugin.ActivateAsync(host.Object);

        await plugin.RefreshFromSettingsAsync();

        var role = GetRole(plugin, "authenticated-cli-opencode");
        Assert.True(role.IsAvailable);
        Assert.Equal("opencode/cached-free", Assert.Single(role.SupportedModels).Id);
        Assert.True(plugin.GetOpenCodeCatalogStatus().IsLastKnownGood);
        Assert.NotNull(plugin.GetOpenCodeCatalogStatus().LastRefreshError);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task OpenCodeCatalogFailureWithoutCache_IsUnavailableAndActionable()
    {
        using var fake = FakeCliInstallation.Create("opencode-catalog-fail-no-cache", "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);

        await plugin.RefreshFromSettingsAsync();

        var role = GetRole(plugin, "authenticated-cli-opencode");
        Assert.Equal(
            CliAvailabilityState.ModelCatalogUnavailable,
            plugin.GetSnapshot(Descriptor(CliProviderKind.OpenCode)).State);
        Assert.False(role.IsAvailable);
        Assert.Empty(role.SupportedModels);
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData("default")]
    [InlineData("anthropic/claude")]
    [InlineData("opencode/paid-model")]
    [InlineData("opencode/stale-free")]
    public async Task OpenCodeRejectsNonCurrentOrPaidModelBeforeRequestLaunch(string model)
    {
        using var fake = FakeCliInstallation.Create("opencode-reject-model", "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Instruction",
            "Input",
            model,
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.InvalidRequest, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.False(File.Exists(fake.CapturePath));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void OpenCodeJsonlParser_UsesLastTextPartAndRejectsIncompleteOrExtraLogicalFields()
    {
        var descriptor = Descriptor(CliProviderKind.OpenCode);
        Assert.Equal(
            "last",
            descriptor.ParseSuccessfulOutput(
                "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"first\\\"}\"}}\n"
                + "{\"type\":\"reasoning\",\"part\":{\"type\":\"reasoning\"}}\n"
                + "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"last\\\"}\"}}"));
        Assert.ThrowsAny<JsonException>(() => descriptor.ParseSuccessfulOutput("plain text"));
        Assert.Throws<CliProtocolException>(() => descriptor.ParseSuccessfulOutput(
            "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"ok\\\",\\\"extra\\\":true}\"}}"));
        Assert.Throws<CliProtocolException>(() => descriptor.ParseSuccessfulOutput(
            "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"earlier\\\"}\"}}\n"
            + "{\"type\":\"text\",\"part\":{\"type\":\"text\"}}"));
        Assert.Throws<CliProtocolException>(() => descriptor.ParseSuccessfulOutput(
            "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"earlier\\\"}\"}}\n"
            + "{\"type\":\"text\"}"));
        Assert.ThrowsAny<JsonException>(() => descriptor.ParseSuccessfulOutput("{\"type\":\"text\""));
    }

    [Fact]
    public void OpenCodeFailureClassificationInput_UsesOnlyExplicitErrorFields()
    {
        var descriptor = Descriptor(CliProviderKind.OpenCode);
        Assert.Equal(
            "rate limit exceeded",
            descriptor.ExtractFailureText(
                "{\"type\":\"error\",\"error\":{\"data\":{\"message\":\"rate limit exceeded\"}}}",
                "prompt secret: authentication failed"));
        Assert.Equal(
            "login required",
            descriptor.ExtractFailureText(
                "",
                "{\"error\":{\"message\":\"login required\"}}"));
        Assert.Equal("", descriptor.ExtractFailureText("untrusted prompt text", "plain error"));
    }

    [Fact]
    public async Task OpenCodeNeverWritesPromptOrResultContentToHostLogs()
    {
        using var fake = FakeCliInstallation.Create("opencode-invalid-json", "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var logs = new List<string>();
        var host = CreateHost();
        host.Setup(service => service.Log(It.IsAny<PluginLogLevel>(), It.IsAny<string>()))
            .Callback<PluginLogLevel, string>((_, message) => logs.Add(message));
        await plugin.ActivateAsync(host.Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");
        const string secretInput = "TYPEWHISPER_PRIVATE_PROMPT_SENTINEL";

        await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync(
            "Do not log the source text.",
            secretInput,
            Assert.Single(role.SupportedModels).Id,
            CancellationToken.None));

        var combinedLogs = string.Join('\n', logs);
        Assert.DoesNotContain(secretInput, combinedLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("processed", combinedLogs, StringComparison.Ordinal);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public void OpenCodeEnvironment_PreservesOnlySafeExplicitAuthenticationDataPath()
    {
        using var fake = FakeCliInstallation.Create("opencode-environment", "opencode.exe");
        var safeData = Path.Join(fake.WorkingDirectory, "data");
        Directory.CreateDirectory(safeData);
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", safeData);
        try
        {
            var request = CreateRunnerRequest(fake, "", TimeSpan.FromSeconds(5)) with
            {
                ProviderEnvironmentVariables = ["XDG_DATA_HOME"],
                EnvironmentOverrides = AuthenticatedCliPlugin.CreateOpenCodeEnvironmentOverrides(fake.WorkingDirectory),
                RestrictUserDirectories = true
            };
            var startInfo = CliProcessRunner.CreateStartInfo(request);

            Assert.Equal(safeData, startInfo.Environment["XDG_DATA_HOME"]);
            Assert.Equal(fake.WorkingDirectory, startInfo.Environment["TEMP"]);
            Assert.False(startInfo.Environment.ContainsKey("APPDATA"));
            Assert.False(startInfo.Environment.ContainsKey("LOCALAPPDATA"));
            Assert.False(startInfo.Environment.ContainsKey("HOMEPATH"));
            Assert.Throws<InvalidOperationException>(() => CliProcessRunner.CreateStartInfo(request with
            {
                EnvironmentOverrides = new Dictionary<string, string> { ["BAD=NAME"] = "value" }
            }));
            Assert.Throws<InvalidOperationException>(() => CliProcessRunner.CreateStartInfo(request with
            {
                EnvironmentOverrides = new Dictionary<string, string> { ["PATH"] = "unsafe" }
            }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }

    [Fact]
    public async Task CatalogCapabilityNotification_IsPublishedAfterReleasingRefreshGate()
    {
        using var fake = FakeCliInstallation.Create("opencode-notification", "opencode.exe");
        using var plugin = CreatePlugin(fake.DirectoryPath);
        var host = CreateHost();
        var nestedRefreshes = 0;
        host.Setup(service => service.NotifyCapabilitiesChanged()).Callback(() =>
        {
            if (Interlocked.Increment(ref nestedRefreshes) == 1)
                plugin.RefreshFromSettingsAsync().Wait(TimeSpan.FromSeconds(10));
        });
        await plugin.ActivateAsync(host.Object);

        await plugin.RefreshFromSettingsAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(nestedRefreshes > 0);
        Assert.Equal(
            "opencode/muse-spark-1.3-contributor-free",
            Assert.Single(GetRole(plugin, "authenticated-cli-opencode").SupportedModels).Id);
        await plugin.DeactivateAsync();
    }

    [LiveOpenCodeFact]
    public async Task LiveOpenCodeProviderWithMuseSparkFree()
    {
        using var plugin = CreatePlugin(Environment.GetEnvironmentVariable("PATH") ?? "");
        await plugin.ActivateAsync(CreateHost().Object);
        await plugin.RefreshFromSettingsAsync();
        var role = GetRole(plugin, "authenticated-cli-opencode");
        const string model = "opencode/muse-spark-1.3-contributor-free";
        Assert.Contains(role.SupportedModels, entry => string.Equals(entry.Id, model, StringComparison.Ordinal));

        var result = await role.ProcessAsync(
            "Return exactly {\"text\":\"TYPEWHISPER_OPENCODE_OK\"}. Do not add any other text.",
            "Synthetic TypeWhisper live validation input.",
            model,
            CancellationToken.None);

        Assert.Equal("TYPEWHISPER_OPENCODE_OK", result);
        await plugin.DeactivateAsync();
    }

    private static string VerboseModel(
        string headerId,
        string name,
        string cost,
        string status = "active",
        string providerId = "opencode",
        string? metadataId = null,
        string inputModalities = "[\"text\"]",
        string outputModalities = "[\"text\"]",
        string variants = "{}") =>
        $"opencode/{headerId}\n{{\"id\":{JsonSerializer.Serialize(metadataId ?? headerId)},"
        + $"\"providerID\":{JsonSerializer.Serialize(providerId)},\"name\":{JsonSerializer.Serialize(name)},"
        + $"\"status\":{JsonSerializer.Serialize(status)},\"modalities\":{{\"input\":{inputModalities},\"output\":{outputModalities}}},"
        + $"\"cost\":{cost},\"variants\":{variants}}}";

    private sealed class LiveOpenCodeFactAttribute : FactAttribute
    {
        public LiveOpenCodeFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("TYPEWHISPER_LIVE_OPENCODE_TEST"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set TYPEWHISPER_LIVE_OPENCODE_TEST=1 to run the live OpenCode Zen test.";
            }
        }
    }

    private sealed class RecordingCliRunner(string standardOutput) : ICliProcessRunner
    {
        internal CliProcessRequest? Request { get; private set; }

        public Task<CliProcessResult> RunAsync(
            CliProcessRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new CliProcessResult(
                0,
                standardOutput,
                "",
                TimeSpan.Zero,
                Encoding.UTF8.GetByteCount(standardOutput),
                0));
        }
    }

    private static AuthenticatedCliPlugin CreatePlugin(string processPath) =>
        new(
            new CliExecutableDiscovery(target =>
                target == EnvironmentVariableTarget.Process ? processPath : null),
            new CliProcessRunner());

    private static Mock<IPluginHostServices> CreateHost()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(service => service.GetSetting<string>(It.IsAny<string>())).Returns((string?)null);
        return host;
    }

    private static ILlmProviderPlugin GetRole(AuthenticatedCliPlugin plugin, string selectionId) =>
        plugin.AdditionalLlmProviders.Single(provider =>
            string.Equals(
                ((ILlmProviderSelectionIdentity)provider).LlmSelectionId,
                selectionId,
                StringComparison.Ordinal));

    private static CliProviderDescriptor Descriptor(CliProviderKind kind) =>
        CliProviderDescriptor.All.Single(descriptor => descriptor.Kind == kind);

    private static CliProcessRequest CreateRunnerRequest(
        FakeCliInstallation fake,
        string standardInput,
        TimeSpan timeout) =>
        new(
            fake.ExecutablePath("codex.exe"),
            ["exec", "--json", "-"],
            standardInput,
            fake.WorkingDirectory,
            ["CODEX_HOME"],
            timeout,
            AuthenticatedCliPlugin.MaximumStandardOutputBytes,
            AuthenticatedCliPlugin.MaximumStandardErrorBytes);

    private static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class FakeCliInstallation : IDisposable
    {
        private readonly string _root;

        private FakeCliInstallation(string scenario)
        {
            _root = Path.Combine(Path.GetTempPath(), "TypeWhisperAuthenticatedCliTests", Guid.NewGuid().ToString("N"));
            DirectoryPath = Path.Combine(_root, scenario);
            WorkingDirectory = Path.Combine(_root, "working");
            Directory.CreateDirectory(DirectoryPath);
            Directory.CreateDirectory(WorkingDirectory);
        }

        internal string DirectoryPath { get; }
        internal string WorkingDirectory { get; }
        internal string CapturePath => Path.Combine(DirectoryPath, "capture.json");

        internal static FakeCliInstallation Create(string scenario, string executableName)
        {
            var installation = new FakeCliInstallation(scenario);
            installation.Install(executableName);
            return installation;
        }

        internal static FakeCliInstallation CreateEmpty(string scenario) => new(scenario);

        internal void Install(string executableName)
        {
            var source = FakeOutputDirectory();
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), overwrite: true);

            File.Copy(
                Path.Combine(source, "TypeWhisper.FakeProviderCli.exe"),
                ExecutablePath(executableName),
                overwrite: true);
        }

        internal string ExecutablePath(string executableName) => Path.Combine(DirectoryPath, executableName);

        private static string FakeOutputDirectory()
        {
            var testAssembly = new FileInfo(typeof(AuthenticatedCliPluginTests).Assembly.Location);
            var configuration = testAssembly.Directory!.Parent!.Name;
            var outputRoot = Path.Combine(
                RepositoryRoot(),
                "tests",
                "TypeWhisper.FakeProviderCli",
                "bin");
            var candidate = new[] { configuration, "Debug", "Release" }
                .Distinct()
                .Select(candidateConfiguration => Path.Combine(
                    outputRoot,
                    candidateConfiguration,
                    "net10.0-windows10.0.19041.0"))
                .Where(candidateDirectory => File.Exists(Path.Combine(
                    candidateDirectory,
                    "TypeWhisper.FakeProviderCli.exe")))
                .OrderByDescending(candidateDirectory => File.GetLastWriteTimeUtc(Path.Combine(
                    candidateDirectory,
                    "TypeWhisper.FakeProviderCli.exe")))
                .FirstOrDefault();
            if (candidate is not null)
                return candidate;

            throw new DirectoryNotFoundException("Could not locate the built fake provider CLI.");
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(_root))
                        Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 9)
                        Thread.Sleep(50);
                }
            }
        }
    }
}
