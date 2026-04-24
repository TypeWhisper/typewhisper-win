using System.IO;

namespace TypeWhisper.Windows.Services;

public sealed record CliInstallState(
    bool BundledCliAvailable,
    bool Installed,
    string? BundledPath,
    string InstallPath,
    bool InstallDirectoryInPath,
    string StatusText);

public sealed class CliInstallService
{
    private const string CliFileName = "typewhisper.exe";
    private readonly Func<string?> _bundledPathProvider;
    private readonly Func<string> _installDirectoryProvider;
    private readonly Action<string> _pathUpdater;

    public CliInstallService()
        : this(FindBundledCliPath, DefaultInstallDirectory, AddUserPathEntry)
    {
    }

    internal CliInstallService(
        Func<string?> bundledPathProvider,
        Func<string> installDirectoryProvider,
        Action<string> pathUpdater)
    {
        _bundledPathProvider = bundledPathProvider;
        _installDirectoryProvider = installDirectoryProvider;
        _pathUpdater = pathUpdater;
    }

    public CliInstallState GetState()
    {
        var installDirectory = _installDirectoryProvider();
        var installPath = GetCliInstallPath(installDirectory);
        var bundledPath = _bundledPathProvider();
        var installed = FileExistsWithExactName(installPath);
        var inPath = IsDirectoryInPath(installDirectory);

        var status = installed
            ? inPath
                ? $"Installed at {installPath}"
                : $"Installed at {installPath}; restart shells after PATH update"
            : bundledPath is null
                ? "CLI binary not found in this build"
                : "Not installed";

        return new CliInstallState(
            bundledPath is not null,
            installed,
            bundledPath,
            installPath,
            inPath,
            status);
    }

    public CliInstallState Install()
    {
        var state = GetState();
        if (state.BundledPath is null)
            return state;

        var installDirectory = Path.GetDirectoryName(state.InstallPath)
            ?? throw new InvalidOperationException("Missing CLI install directory.");

        Directory.CreateDirectory(installDirectory);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(state.BundledPath)!, "typewhisper.*")
            .Where(file => Path.GetFileName(file).StartsWith("typewhisper.", StringComparison.Ordinal)))
        {
            File.Copy(file, Path.Combine(installDirectory, Path.GetFileName(file)), overwrite: true);
        }

        _pathUpdater(installDirectory);

        return GetState();
    }

    public static IReadOnlyList<string> BuildCliExamples(int port) =>
    [
        "typewhisper --help",
        $"typewhisper status --port {port}",
        $"typewhisper models --port {port}",
        $"typewhisper transcribe recording.wav --port {port}",
        $"typewhisper transcribe recording.wav --language de --json --port {port}"
    ];

    public static IReadOnlyList<string> BuildCurlExamples(int port) =>
    [
        $"curl http://localhost:{port}/v1/status",
        $"curl http://localhost:{port}/v1/models",
        $"curl -X POST http://localhost:{port}/v1/transcribe -F \"file=@recording.wav\"",
        $"curl -X POST http://localhost:{port}/v1/dictation/start",
        $"curl -X POST http://localhost:{port}/v1/dictation/stop"
    ];

    private static string DefaultInstallDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper",
            "Cli");

    private static string GetCliInstallPath(string installDirectory)
    {
        if (Path.IsPathRooted(CliFileName))
            throw new InvalidOperationException("CLI file name must be relative.");

        return Path.Combine(installDirectory, CliFileName);
    }

    private static string? FindBundledCliPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Cli", CliFileName),
            Path.Combine(baseDirectory, "..", "TypeWhisper.Cli", CliFileName),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "TypeWhisper.Cli", "bin", "Debug", "net10.0", CliFileName),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "TypeWhisper.Cli", "bin", "Release", "net10.0", CliFileName)
        };

        return candidates
            .Select(path => Path.GetFullPath(path))
            .FirstOrDefault(IsCliAppHost);
    }

    private static bool IsCliAppHost(string path) =>
        FileExistsWithExactName(path);

    private static bool FileExistsWithExactName(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
            return false;

        return Directory.EnumerateFiles(directory, fileName)
            .Any(candidate => string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal));
    }

    private static bool IsDirectoryInPath(string directory)
    {
        var full = NormalizeDirectory(directory);
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";

        return SplitPath(userPath).Concat(SplitPath(processPath))
            .Select(NormalizeDirectory)
            .Any(path => string.Equals(path, full, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddUserPathEntry(string directory)
    {
        if (IsDirectoryInPath(directory))
            return;

        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var separator = string.IsNullOrWhiteSpace(current) || current.EndsWith(Path.PathSeparator)
            ? ""
            : Path.PathSeparator.ToString();
        var updated = current + separator + directory;

        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);

        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.Process);
    }

    private static IEnumerable<string> SplitPath(string value) =>
        value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeDirectory(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
