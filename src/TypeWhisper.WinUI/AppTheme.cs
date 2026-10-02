using Microsoft.UI.Xaml;
using Microsoft.Win32;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// The theme is fixed for the lifetime of the process: most brushes are read once in code, so a new choice, or a
// changed Windows app mode, applies after a restart.
internal static class AppTheme
{
    internal static InterfaceThemeStore Store { get; } = new(WinUIProfile.DataPath("interface-theme.json"));

    /// <summary>The choice the running app started with.</summary>
    internal static InterfaceTheme Applied { get; private set; }

    /// <summary>Applies the saved theme, or the Windows app mode. Call before any window exists.</summary>
    internal static void Apply(Application app)
    {
        Applied = Store.Saved;
        app.RequestedTheme = Applied switch
        {
            InterfaceTheme.Light => ApplicationTheme.Light,
            InterfaceTheme.Dark => ApplicationTheme.Dark,
            _ => WindowsUsesLightApps() ? ApplicationTheme.Light : ApplicationTheme.Dark
        };
    }

    // Windows is light until the user chooses the dark app mode.
    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }
}
