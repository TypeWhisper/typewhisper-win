using TypeWhisper.Core.Services.UserData;

namespace TypeWhisper.WinUI;

// "Delete all data" empties the profile folder and, in release builds, the places the 1.0 upgrade copied from,
// which still hold the old history, models and keys: the whole 1.0 data folder, and the data entries 1.0 kept in
// its install folder. That install folder (LocalAppData\TypeWhisper) also holds the installed app, so only the
// named data entries go; Update.exe, current and packages stay. Export all data copies those places too. The
// unhandled-exception log and Wispr Flow import copies in the temp folder are outside the profile and hold
// exception details or a copied transcript database, so they go too.
internal static class UserDataDeletion
{
    private const int Passes = 3;
    private static readonly TimeSpan PassDelay = TimeSpan.FromMilliseconds(500);

    internal static readonly string[] LegacyInstallDataEntries =
    [
        "settings.json", "Data", "Logs", "Models", "Plugins", "PluginData", "Audio", "DictationRecovery",
        "api-port", "api-discovery.json", "api-token",
    ];

    // Development and public Store beta builds neither export nor delete the 1.0 folders.
    internal static ErasureTarget[] PreviousVersionData
    {
        get
        {
            if (!WinUIProfile.UsesLegacyData) return [];
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return
            [
                new(Path.Join(localData, "TypeWhisper-UserData")),
                new(Path.Join(localData, "TypeWhisper"), LegacyInstallDataEntries),
            ];
        }
    }

    internal static ErasureTarget[] Targets =>
    [
        .. PreviousVersionData,
        ErasureTarget.Entry(WinUIProfile.LegacyErrorLogPath),
    ];

    // The previous process lets go of its files a moment after it ends, so a few passes a moment apart.
    internal static ProfileErasureReport FinishPending()
    {
        ProfileErasureReport? report = null;
        for (var pass = 0; pass < Passes; pass++)
        {
            if (pass > 0) Thread.Sleep(PassDelay);
            // Import copies follow their own lease, so they are deleted by their owner; copies left keep the marker.
            report = ProfileDataEraser.CompletePendingErasure(WinUIProfile.Root, () => StableImportCopy.DeleteCopies(), Targets);
            if (report is null or { Complete: true }) break;
        }
        return report ?? new(0, 0, false);
    }
}
