namespace TypeWhisper.WinUI;

// Debug smoke runs can persist across restarts without touching the normal development profile.
internal static class WinUIProfile
{
#if DEBUG
    internal const bool DevelopmentBuild = true;
#else
    internal const bool DevelopmentBuild = false;
#endif
#if TYPEWHISPER_STORE_BETA
    internal const bool StoreBetaBuild = true;
#else
    internal const bool StoreBetaBuild = false;
#endif
    internal static string DisplayName => StoreBetaBuild ? "TypeWhisper Beta" : "TypeWhisper";
    internal static bool UsesLegacyData => !DevelopmentBuild && !StoreBetaBuild;
    internal static string InstanceKey => DevelopmentBuild ? "TypeWhisper.WinUI.Dev.Primary"
        : StoreBetaBuild ? "TypeWhisper.WinUI.StoreBeta.Primary" : "TypeWhisper.WinUI.Primary";
    internal static string LegacyErrorLogPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        StoreBetaBuild ? "TypeWhisper-WinUI-StoreBeta-errors.log" : "TypeWhisper-WinUI-errors.log");
    internal static string? CliInstallDirectory => StoreBetaBuild
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-Beta", "1.1", "Cli")
        : null;
    private static readonly string? TestName = GetTestName();
    internal static bool IsTestProfile => TestName is not null;
    internal static string Root { get; } = ResolveRoot(TestName,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), System.IO.Path.GetTempPath(), DevelopmentBuild, StoreBetaBuild);

    internal static string DataPath(params string[] segments) => System.IO.Path.Combine([Root, .. segments]);

    internal static string PluginAssetPath(string pluginId) => IsTestProfile || !DevelopmentBuild
        ? DataPath("PluginData", pluginId)
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper-DevUserData", "PluginData", pluginId);

    internal static string ResolveRoot(string? testName, string localData, string temporary, bool development = true, bool storeBeta = false)
    {
        // Production never honors test-profile overrides or opens development data.
        if (!development) return System.IO.Path.Combine(localData, storeBeta ? "TypeWhisper-WinUI-StoreBeta" : "TypeWhisper-WinUI");
        if (string.IsNullOrEmpty(testName)) return System.IO.Path.Combine(localData, "TypeWhisper-WinUI-DevUserData");
        if (testName.Length > 64 || testName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Test profile names must contain 1–64 ASCII letters, digits, hyphens or underscores.", nameof(testName));
        return System.IO.Path.Combine(temporary, "TypeWhisper-WinUI-TestProfiles", testName);
    }

    private static string? GetTestName()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_TEST_PROFILE");
#else
        return null;
#endif
    }
}
