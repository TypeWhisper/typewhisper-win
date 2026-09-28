using TypeWhisper.Core.Services.UserData;

namespace TypeWhisper.WinUI;

// "Delete all data" empties the profile folder and, in release builds, the 1.0 data folder the upgrade copied
// from, which still holds the old history and models. The 1.0 install folder (LocalAppData\TypeWhisper) is never
// touched: it can hold the installed app itself.
internal static class UserDataDeletion
{
    private const int Passes = 3;
    private static readonly TimeSpan PassDelay = TimeSpan.FromMilliseconds(500);

    internal static string[] AdditionalRoots =>
#if DEBUG
        [];
#else
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-UserData")];
#endif

    // The previous process lets go of its files a moment after it ends, so a few passes a moment apart.
    internal static ProfileErasureReport FinishPending()
    {
        ProfileErasureReport? report = null;
        for (var pass = 0; pass < Passes; pass++)
        {
            if (pass > 0) Thread.Sleep(PassDelay);
            report = ProfileDataEraser.CompletePendingErasure(WinUIProfile.Root, AdditionalRoots);
            if (report is null or { Complete: true }) break;
        }
        DeleteCrashLog();
        return report ?? new(0, 0, false);
    }

    // The unhandled-exception log sits in the temp folder, outside the profile, and holds exception details.
    private static void DeleteCrashLog()
    {
        try { File.Delete(Path.Combine(Path.GetTempPath(), "TypeWhisper-WinUI-errors.log")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
