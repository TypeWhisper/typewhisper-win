using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class CliInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TypeWhisper-cli-test-" + Guid.NewGuid());
    private readonly string _bundle;
    private readonly string _install;
    private string _userPath = "C:\\Other;C:\\Legacy\\Cli";
    public CliInstallationTests()
    {
        _bundle = Path.Combine(_root, "bundle");
        _install = Path.Combine(_root, "installed");
        Directory.CreateDirectory(_bundle);
        File.WriteAllText(Path.Combine(_bundle, "typewhisper.exe"), "cli");
        File.WriteAllText(Path.Combine(_bundle, "typewhisper.runtimeconfig.json"), "runtime");
    }
    private CliInstallation Service() => new(_bundle, _install, () => _userPath, value => _userPath = value, Path.Combine(_root, "profile"));

    private void Shared(string name, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(Path.Combine(_root, name), bytes);
        File.WriteAllText(Path.Combine(_bundle, ".typewhisper-shared-runtime.json"),
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            { [name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) }));
    }

    [Fact]
    public void SharedRuntimeIsCopiedIntoIndependentInstallationAndRemovedAsOwned()
    {
        Shared("coreclr.dll", "shared runtime");
        var service = Service();
        service.Install();
        File.Delete(Path.Combine(_root, "coreclr.dll"));
        Assert.Equal("shared runtime", File.ReadAllText(Path.Combine(_install, "coreclr.dll")));
        Assert.False(File.Exists(Path.Combine(_install, ".typewhisper-shared-runtime.json")));
        service.Remove();
        Assert.False(File.Exists(Path.Combine(_install, "coreclr.dll")));
    }

    [Fact]
    public void ChangedSharedRuntimeFailsBeforeWritingInstallationOrPath()
    {
        Shared("coreclr.dll", "expected");
        File.WriteAllText(Path.Combine(_root, "coreclr.dll"), "changed");
        var previousPath = _userPath;
        Assert.Throws<IOException>(() => Service().Install());
        Assert.False(Directory.Exists(_install));
        Assert.Equal(previousPath, _userPath);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("..\\outside.dll")]
    [InlineData("C:outside.dll")]
    [InlineData("cli-profile.json")]
    public void SharedManifestRejectsPathsOutsideRuntimePayload(string name)
    {
        File.WriteAllText(Path.Combine(_bundle, ".typewhisper-shared-runtime.json"),
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [name] = new('a', 64) }));
        Assert.Throws<IOException>(() => Service().Install());
        Assert.False(Directory.Exists(_install));
    }

    [Fact]
    public void SharedRuntimeUpdateReplacesOnlyPreviouslyOwnedPayload()
    {
        Shared("coreclr.dll", "old runtime");
        var service = Service();
        service.Install();
        Shared("coreclr.dll", "new runtime");
        service.Install();
        Assert.Equal("new runtime", File.ReadAllText(Path.Combine(_install, "coreclr.dll")));
        File.WriteAllText(Path.Combine(_install, "coreclr.dll"), "external change");
        Assert.Throws<IOException>(service.Install);
        service.Remove();
        Assert.Equal("external change", File.ReadAllText(Path.Combine(_install, "coreclr.dll")));
    }

    [Fact]
    public void InstallCopiesRuntimeAndProfileAndRemovePreservesOtherFilesAndPath()
    {
        var originalPath = _userPath;
        var service = Service();
        service.Install();
        Assert.True(service.GetState().Installed);
        Assert.True(service.GetState().InPath);
        Assert.Equal("runtime", File.ReadAllText(Path.Combine(_install, "typewhisper.runtimeconfig.json")));
        Assert.Contains("profile_directory", File.ReadAllText(Path.Combine(_install, "cli-profile.json")));
        File.WriteAllText(Path.Combine(_install, "keep.txt"), "unrelated");
        service.Remove();
        Assert.False(service.GetState().Installed);
        Assert.Equal(originalPath, _userPath);
        Assert.True(File.Exists(Path.Combine(_install, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(_install, "cli-profile.json")));
    }

    [Fact]
    public void UpdateReplacesOwnedFilesWithoutDuplicatingPath()
    {
        var service = Service();
        service.Install();
        var path = _userPath;
        File.WriteAllText(Path.Combine(_bundle, "typewhisper.exe"), "new cli");
        service.Install();
        Assert.Equal("new cli", File.ReadAllText(Path.Combine(_install, "typewhisper.exe")));
        Assert.Equal(path, _userPath);
    }

    [Fact]
    public void PreexistingPathIsNotRemoved()
    {
        _userPath += ";\"" + _install.ToUpperInvariant() + "\\\"";
        var original = _userPath;
        Service().Install();
        Service().Remove();
        Assert.Equal(original, _userPath);
    }

    [Fact]
    public void UnownedCollisionIsNeverOverwritten()
    {
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "typewhisper.exe"), "other cli");
        Assert.Throws<IOException>(() => Service().Install());
        Assert.Equal("other cli", File.ReadAllText(Path.Combine(_install, "typewhisper.exe")));
    }

    [Fact]
    public void ModifiedOwnedFilesSurviveRemoval()
    {
        Service().Install();
        File.WriteAllText(Path.Combine(_install, "typewhisper.exe"), "replaced by user");
        Service().Remove();
        Assert.Equal("replaced by user", File.ReadAllText(Path.Combine(_install, "typewhisper.exe")));
    }

    [Fact]
    public void MissingBundleDoesNotChangeUserPath()
    {
        File.Delete(Path.Combine(_bundle, "typewhisper.exe"));
        var original = _userPath;
        Assert.False(Service().GetState().Bundled);
        Assert.Throws<IOException>(() => Service().Install());
        Assert.Equal(original, _userPath);
    }

    [Fact]
    public async Task RealBundleCanBeInstalledRunUpdatedAndRemovedWhenProvided()
    {
        var source = Environment.GetEnvironmentVariable("TYPEWHISPER_CLI_TEST_BUNDLE");
        if (string.IsNullOrWhiteSpace(source)) return;
        Assert.True(File.Exists(Path.Combine(source, "typewhisper.exe")), "The provided bundle must contain typewhisper.exe.");
        var bundle = Path.Combine(_root, "real-bundle");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(bundle, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        var profile = Path.Combine(_root, "isolated-profile");
        var originalPath = _userPath;
        var service = new CliInstallation(bundle, _install, () => _userPath, value => _userPath = value, profile);
        service.Install();
        Assert.True(service.GetState().Installed);
        Assert.True(service.GetState().InPath);
        using (var binding = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_install, "cli-profile.json"))))
            Assert.Equal(profile, binding.RootElement.GetProperty("profile_directory").GetString());
        var unrelated = Path.Combine(_install, "keep.txt");
        File.WriteAllText(unrelated, "unrelated");

        async Task RunHelp()
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(_install, "typewhisper.exe"))
            {
                WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--help");
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("The installed CLI did not finish --help within 30 seconds.");
            }
            var output = await stdout;
            var error = await stderr;
            Assert.True(process.ExitCode == 0, $"CLI exited with {process.ExitCode}: {error}");
            Assert.Contains("typewhisper", output, StringComparison.OrdinalIgnoreCase);
        }
        await RunHelp();
        var installedPath = _userPath;
        service.Install();
        Assert.Equal(installedPath, _userPath);
        await RunHelp();
        service.Remove();
        Assert.False(service.GetState().Installed);
        Assert.False(service.GetState().CanRemove);
        Assert.False(File.Exists(Path.Combine(_install, "cli-profile.json")));
        Assert.Equal("unrelated", File.ReadAllText(unrelated));
        Assert.Equal(originalPath, _userPath);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void JournalRecoversBeforeAndAfterAtomicReplace(bool updating, bool replaced, bool remove)
    {
        var service = Service();
        if (updating) service.Install();
        else Directory.CreateDirectory(_install);
        var manifestPath = Path.Combine(_install, ".typewhisper-cli.json");
        var manifest = updating
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject()
            : new System.Text.Json.Nodes.JsonObject { ["Files"] = new System.Text.Json.Nodes.JsonObject() };
        // Reproduce persisted state at either side of the file replacement,
        // before the final manifest commit could run.
        var updatedBytes = System.Text.Encoding.UTF8.GetBytes("updated cli");
        var pendingHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(updatedBytes));
        manifest["PendingFiles"] = new System.Text.Json.Nodes.JsonObject { ["typewhisper.exe"] = pendingHash };
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        if (replaced) File.WriteAllBytes(Path.Combine(_install, "typewhisper.exe"), updatedBytes);
        File.WriteAllText(Path.Combine(_install, "keep.txt"), "unrelated");
        // Recover with a still newer bundle to verify pending ownership survives
        // replacing the interrupted update, rather than only retrying its bytes.
        File.WriteAllText(Path.Combine(_bundle, "typewhisper.exe"), "latest cli");
        if (remove)
        {
            service.Remove();
            Assert.False(File.Exists(Path.Combine(_install, "typewhisper.exe")));
        }
        else
        {
            service.Install();
            Assert.Equal("latest cli", File.ReadAllText(Path.Combine(_install, "typewhisper.exe")));
            using var committed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Empty(committed.RootElement.GetProperty("PendingFiles").EnumerateObject());
        }
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(_install, "keep.txt")));
    }

    [Fact]
    public void JournalDoesNotAuthorizeAnUnrelatedReplacement()
    {
        Service().Install();
        var manifestPath = Path.Combine(_install, ".typewhisper-cli.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["PendingFiles"] = new System.Text.Json.Nodes.JsonObject { ["typewhisper.exe"] = "some-other-hash" };
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.WriteAllText(Path.Combine(_install, "typewhisper.exe"), "unrelated replacement");
        Assert.Throws<IOException>(() => Service().Install());
        Service().Remove();
        Assert.Equal("unrelated replacement", File.ReadAllText(Path.Combine(_install, "typewhisper.exe")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
