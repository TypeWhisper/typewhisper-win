using System.Diagnostics;

namespace TypeWhisper.WinUI;

// Shows a file in File Explorer. Explorer is started from the Windows folder by its full path rather than looked up by
// name, so a program called explorer.exe in the working directory or on the search path is never started instead.
internal static class FileExplorer
{
    internal static string ExecutablePath =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>The start information that opens the folder of <paramref name="path"/> with the file selected.</summary>
    internal static ProcessStartInfo SelectFileStartInfo(string path)
    {
        var fullPath = Path.GetFullPath(path);
        // Windows paths cannot contain quotes, so the quoted path is a single argument. Explorer reads /select, with the
        // path in quotes right after the comma rather than the usual argument quoting.
        if (fullPath.Contains('"')) throw new ArgumentException("The path contains a quote.", nameof(path));
        return new ProcessStartInfo(ExecutablePath, $"/select,\"{fullPath}\"") { UseShellExecute = false };
    }

    /// <summary>Opens the folder of <paramref name="path"/> in File Explorer with the file selected.</summary>
    internal static void SelectFile(string path) => Process.Start(SelectFileStartInfo(path))?.Dispose();
}
