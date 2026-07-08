using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.FileIO;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
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
    public bool TryMoveDirectoryToRecycleBin(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return true;

        try
        {
            FileSystem.DeleteDirectory(
                fullPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.DoNothing);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not move directory to the Recycle Bin: {0}", ex.Message);
            return false;
        }

        return !Directory.Exists(fullPath);
    }
}
