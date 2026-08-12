using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.AuthenticatedCli;
using TypeWhisper.PluginSDK;

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
