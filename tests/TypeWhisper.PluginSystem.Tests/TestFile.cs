using System.IO;

namespace TypeWhisper.PluginSystem.Tests;

internal static class TestFile
{
    public static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(ProjectFile(parts));

    public static string ProjectFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Join([directory.FullName, .. parts]);
    }

    public static string ExtractBlock(string text, string marker, int maxLength = 1800)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find {marker}.");
        var length = Math.Min(maxLength, text.Length - start);
        return text.Substring(start, length);
    }

    public static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
