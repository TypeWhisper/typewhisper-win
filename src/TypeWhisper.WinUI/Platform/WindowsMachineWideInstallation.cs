using Microsoft.Win32;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

/// <summary>Whether this process runs from the per-machine MSI installation; read once per launch.</summary>
internal static class WindowsMachineWideInstallation
{
    private static readonly Lazy<bool> s_current = new(Detect);

    internal static bool Current => s_current.Value;

    private static bool Detect()
    {
#if DEBUG || TYPEWHISPER_STORE
        return false;
#else
        try
        {
            var locator = Velopack.Locators.VelopackLocator.Current;
            if (string.IsNullOrWhiteSpace(locator.AppId) || locator.IsPortable) return false;
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(MachineWideInstallation.UninstallKey(locator.AppId), writable: false);
            return MachineWideInstallation.Matches(key?.GetValue("InstallLocation") as string, locator.RootAppDir);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write("app.install.machine-wide-check-failed", ex);
            return false;
        }
#endif
    }
}
