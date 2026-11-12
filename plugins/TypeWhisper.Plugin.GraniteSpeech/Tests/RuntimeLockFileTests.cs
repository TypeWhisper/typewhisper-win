using System.IO;
using System.Text.RegularExpressions;
using TypeWhisper.Plugin.GraniteSpeech;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class RuntimeLockFileTests
{
    private static readonly Regex Requirement = new(@"^(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)==(?<version>[^\s\\;]+)", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new(@"^--hash=sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("Cpu", "requirements-cpu.lock.txt", "cpu")]
    [InlineData("NvidiaCuda", "requirements-cu130.lock.txt", "cu130")]
    [InlineData("Auto", "requirements-cu130.lock.txt", "cu130")]
    public void DeviceSelectsTheMatchingHashedLockFile(string device, string lockFile, string flavor)
    {
        Assert.Equal(lockFile, GraniteSpeechPlugin.RuntimeLockFile(device));
        var arguments = GraniteSpeechPlugin.PipInstallArguments(device, @"C:\plugin\Scripts\" + lockFile);
        Assert.Contains("--require-hashes", arguments);
        Assert.Contains("--no-deps", arguments);
        Assert.Contains("--only-binary :all:", arguments);
        Assert.Contains($"-r \"C:\\plugin\\Scripts\\{lockFile}\"", arguments);
        Assert.Contains($"--index-url https://download.pytorch.org/whl/{flavor} ", arguments);
        Assert.EndsWith("--extra-index-url https://pypi.org/simple/", arguments);
        // Requirements outside the lock file would be installed without a hash.
        Assert.DoesNotContain("torch==", arguments);
    }

    [Theory]
    [InlineData("requirements-cpu.lock.txt", "cpu")]
    [InlineData("requirements-cu130.lock.txt", "cu130")]
    public void LockFilePinsEveryPackageWithHashesAndMatchesTheRequirements(string lockFile, string flavor)
    {
        var entries = ParseLockFile(TestFile.ReadProjectFile("plugins", "TypeWhisper.Plugin.GraniteSpeech", "Scripts", lockFile));
        Assert.True(entries.Count > 10);
        Assert.All(entries, entry => Assert.NotEmpty(entry.Value));
        Assert.Equal(entries.Count, entries.Keys.Select(key => key.Split("==")[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var line in File.ReadLines(TestFile.ProjectFile("plugins", "TypeWhisper.Plugin.GraniteSpeech", "Scripts", "requirements.txt")))
        {
            var requirement = line.Trim();
            if (requirement.Length == 0) continue;
            var expected = requirement.StartsWith("torch==", StringComparison.Ordinal) || requirement.StartsWith("torchaudio==", StringComparison.Ordinal)
                ? requirement + "+" + flavor
                : requirement;
            Assert.Contains(entries.Keys, key => string.Equals(Normalize(key), Normalize(expected), StringComparison.Ordinal));
        }
        Assert.Contains(GraniteSpeechPlugin.RuntimeWheels(flavor == "cpu" ? "Cpu" : "NvidiaCuda").Split(' ')[0], entries.Keys);
    }

    private static string Normalize(string requirement) => requirement.Replace('_', '-').ToLowerInvariant();

    private static Dictionary<string, List<string>> ParseLockFile(string text)
    {
        var entries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\\').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("--hash=", StringComparison.Ordinal))
            {
                Assert.NotNull(current);
                Assert.Matches(Hash, line);
                current.Add(line);
                continue;
            }
            // Only plain name==version pins: no options, URLs, editable or local paths.
            Assert.False(line.StartsWith('-'), "Unexpected option in lock file: " + line);
            var match = Requirement.Match(line);
            Assert.True(match.Success && match.Length == line.Length, "Unexpected lock file line: " + line);
            current = [];
            entries.Add(line, current);
        }
        return entries;
    }
}
