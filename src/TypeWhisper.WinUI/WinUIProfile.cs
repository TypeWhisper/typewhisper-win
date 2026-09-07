namespace TypeWhisper.WinUI;

// Debug smoke runs can persist across restarts without touching the normal development profile.
internal static class WinUIProfile
{
    private static readonly string? TestName = GetTestName();
    internal static bool IsTestProfile => TestName is not null;
    internal static string Root { get; } = ResolveRoot(TestName,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), System.IO.Path.GetTempPath());

    internal static string DataPath(params string[] segments) => System.IO.Path.Combine([Root, .. segments]);

    internal static string PluginAssetPath(string pluginId) => IsTestProfile
        ? DataPath("PluginData", pluginId)
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper-DevUserData", "PluginData", pluginId);

    internal static string ResolveRoot(string? testName, string localData, string temporary)
    {
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
