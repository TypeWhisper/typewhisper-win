namespace TypeWhisper.Core.Tests;

// Tests that read the source tree find the checkout above the test binary.
internal static class Repository
{
    public static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "TypeWhisper.slnx")) && !Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("The repository root was not found.");
        return root;
    }

    // Files below the given folder of the checkout, without build output.
    public static IEnumerable<string> SourceFiles(string folder, string pattern)
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(Root(), folder), pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}obj{separator}") && !file.Contains($"{separator}bin{separator}"));
    }
}
