using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// Showing a recording or History audio in File Explorer starts Explorer from the Windows folder.
public sealed class FileExplorerTests
{
    [Fact]
    public void StartsExplorerFromTheWindowsFolderWithoutTheShell()
    {
        var path = Path.Join(Path.GetTempPath(), "Recordings with spaces", "take 1.wav");

        var info = FileExplorer.SelectFileStartInfo(path);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), info.FileName);
        Assert.True(Path.IsPathFullyQualified(info.FileName));
        Assert.False(info.UseShellExecute);
        Assert.Equal($"/select,\"{Path.GetFullPath(path)}\"", info.Arguments);
    }

    [Fact]
    public void RefusesAPathThatCouldEndTheQuotedArgument() =>
        Assert.Throws<ArgumentException>(() => FileExplorer.SelectFileStartInfo("C:\\a\" /root,C:\\b"));
}
