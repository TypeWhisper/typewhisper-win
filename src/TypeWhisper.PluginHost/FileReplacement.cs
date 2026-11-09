namespace TypeWhisper.PluginHost;

/// <summary>
/// Moves a freshly written file over the file it replaces. Windows refuses to replace a file that any other handle
/// has open, and a virus scanner or indexer often still has the previous write open for a few milliseconds. The move
/// is tried again for about a second before the error is passed on.
/// </summary>
internal static class FileReplacement
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    // About 0.9 seconds in all. In stress runs under heavy load, a held settings file was free again within 0.3 seconds.
    private static readonly int[] RetryDelaysMilliseconds = [10, 20, 50, 100, 200, 500];

    internal static void Move(string source, string destination)
    {
        foreach (var delay in RetryDelaysMilliseconds)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsHeldOpen(ex))
            {
                Thread.Sleep(delay);
            }
        }

        File.Move(source, destination, overwrite: true);
    }

    // An open target fails with "access denied", an open source with a sharing or lock violation.
    private static bool IsHeldOpen(Exception ex) =>
        ex is UnauthorizedAccessException or IOException { HResult: SharingViolation or LockViolation };
}
