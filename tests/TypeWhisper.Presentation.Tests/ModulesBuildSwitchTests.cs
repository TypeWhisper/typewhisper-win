using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// Evaluates the app project with MSBuild to check which modules project it links. Nothing is built.
public sealed class ModulesBuildSwitchTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string AppProject = Path.Combine(RepoRoot, "src", "TypeWhisper.WinUI", "TypeWhisper.WinUI.csproj");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "modules-switch-" + Guid.NewGuid().ToString("N"));

    public ModulesBuildSwitchTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RequiringModulesFailsWhenOnlyTheStubWouldBeLinked()
    {
        var missing = Path.Combine(_root, "missing", "TypeWhisper.Modules.csproj");

        var (exitCode, output) = MSBuild("-t:CheckTypeWhisperModules", "-p:TypeWhisperRequireModules=true", $"-p:TypeWhisperModulesProject={missing}");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("TWMODULES001", output);
    }

    [Fact]
    public void RequiringModulesPassesWhenTheModulesProjectExists()
    {
        var present = CreateModulesProjectFile();

        var (exitCode, output) = MSBuild("-t:CheckTypeWhisperModules", "-p:TypeWhisperRequireModules=true", $"-p:TypeWhisperModulesProject={present}");

        Assert.True(exitCode == 0, output);
    }

    [Fact]
    public void WithoutTheRequirementAMissingModulesProjectLinksTheStub()
    {
        var missing = Path.Combine(_root, "missing", "TypeWhisper.Modules.csproj");

        var (exitCode, output) = MSBuild("-t:CheckTypeWhisperModules", $"-p:TypeWhisperModulesProject={missing}");
        Assert.True(exitCode == 0, output);

        var references = ProjectReferences(missing, out var linked);
        Assert.Equal("false", linked);
        Assert.Contains(references, path => path.EndsWith("TypeWhisper.Modules.Stub.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, path => path.Equals(missing, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnExistingModulesProjectReplacesTheStub()
    {
        var present = CreateModulesProjectFile();

        var references = ProjectReferences(present, out var linked);

        Assert.Equal("true", linked);
        Assert.Contains(references, path => path.Equals(present, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, path => path.EndsWith("TypeWhisper.Modules.Stub.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, path => path.EndsWith("TypeWhisper.ModuleKit.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDefaultModulesLocationIsTheIgnoredExternalFolder()
    {
        var (exitCode, output) = MSBuild("-getProperty:TypeWhisperModulesProject");

        Assert.True(exitCode == 0, output);
        var expected = Path.Combine(RepoRoot, "external", "typewhisper-premium-win", "src", "TypeWhisper.Modules", "TypeWhisper.Modules.csproj");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(output.Trim()));
    }

    // Only its existence matters for the switch; evaluation does not read it.
    private string CreateModulesProjectFile()
    {
        var path = Path.Combine(_root, "present", "TypeWhisper.Modules.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return path;
    }

    private static List<string> ProjectReferences(string modulesProject, out string? linked)
    {
        var (exitCode, output) = MSBuild("-getProperty:TypeWhisperModulesLinked", "-getItem:ProjectReference", $"-p:TypeWhisperModulesProject={modulesProject}");
        Assert.True(exitCode == 0, output);
        using var json = JsonDocument.Parse(output);
        linked = json.RootElement.GetProperty("Properties").GetProperty("TypeWhisperModulesLinked").GetString();
        return json.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
            .Select(item => item.GetProperty("FullPath").GetString()!)
            .ToList();
    }

    private static (int ExitCode, string Output) MSBuild(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = RepoRoot
        };
        foreach (var argument in new[] { "msbuild", AppProject, "-nologo", "-nodeReuse:false", "-p:Configuration=Release" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "MSBuild did not finish.");
        return (process.ExitCode, output + error.Result);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TypeWhisper.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }
}
