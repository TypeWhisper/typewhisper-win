using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

internal interface IRecycleBinOperation
{
    bool TryMoveDirectoryToRecycleBin(string path);
}

internal sealed class UninstallUserDataProtector
{
    private const string RecoveryDirectoryName = "TypeWhisper-Recovered";
    private readonly IRecycleBinOperation _recycleBin;
    private readonly Func<DateTimeOffset> _clock;

    internal UninstallUserDataProtector(IRecycleBinOperation recycleBin, Func<DateTimeOffset> clock)
    {
        _recycleBin = recycleBin;
        _clock = clock;
    }

    public static void ProtectLegacyAudioDirectory()
    {
        var protector = new UninstallUserDataProtector(new ShellRecycleBinOperation(), () => DateTimeOffset.Now);
        protector.ProtectLegacyAudioDirectory(TypeWhisperEnvironment.LegacyAudioPath, DefaultRecoveryRoot);
    }

    internal void ProtectLegacyAudioDirectory(string legacyAudioPath, string recoveryRoot)
    {
        if (!Directory.Exists(legacyAudioPath))
            return;

        try
        {
            if (_recycleBin.TryMoveDirectoryToRecycleBin(legacyAudioPath))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            Trace.TraceWarning("Could not move legacy audio directory to the Recycle Bin: {0}", ex.Message);
        }

        try
        {
            MoveLegacyAudioToRecovery(legacyAudioPath, recoveryRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not move legacy audio directory to recovery: {0}", ex.Message);
        }
    }

    private static string DefaultRecoveryRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CombineWithSafeLeaf(localAppData, RecoveryDirectoryName);
        }
    }

    private void MoveLegacyAudioToRecovery(string legacyAudioPath, string recoveryRoot)
    {
        Directory.CreateDirectory(recoveryRoot);
        var timestamp = _clock().ToString("yyyyMMdd-HHmmss");
        var target = ResolveUniquePath(CombineWithSafeLeaf(recoveryRoot, $"Audio-{timestamp}"));
        Directory.Move(legacyAudioPath, target);
    }

    private static string ResolveUniquePath(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
            return target;

        for (var index = 1; ; index++)
        {
            var candidate = $"{target}-{index}";
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static string CombineWithSafeLeaf(string directory, string leafName)
    {
        var safeLeafName = Path.GetFileName(leafName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(safeLeafName) || Path.IsPathRooted(safeLeafName))
            throw new InvalidOperationException("Invalid recovery directory name.");

        return Path.Join(directory, safeLeafName);
    }
}

internal sealed class ShellRecycleBinOperation : IRecycleBinOperation
{
    // The uninstall fast callback must remain non-interactive; FileSystem.DeleteDirectory can show error dialogs.
    private const uint FileOperationDelete = 0x0003;
    private const ushort FileOperationFlags =
        0x0040 | // FOF_ALLOWUNDO
        0x0010 | // FOF_NOCONFIRMATION
        0x0400 | // FOF_NOERRORUI
        0x0004;  // FOF_SILENT

    public bool TryMoveDirectoryToRecycleBin(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return true;

        var operation = new ShFileOpStruct
        {
            WFunc = FileOperationDelete,
            PFrom = $"{fullPath}\0\0",
            FFlags = FileOperationFlags
        };

        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.FAnyOperationsAborted || Directory.Exists(fullPath))
            Trace.TraceWarning("Could not move directory to the Recycle Bin. Result: {0}, Aborted: {1}", result, operation.FAnyOperationsAborted);

        return result == 0 && !operation.FAnyOperationsAborted && !Directory.Exists(fullPath);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOperation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr HWnd;
        public uint WFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string PFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? PTo;
        public ushort FFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool FAnyOperationsAborted;
        public IntPtr HNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LpszProgressTitle;
    }
}
