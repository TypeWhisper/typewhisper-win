using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TypeWhisper.Core;
using Velopack.Locators;
using Velopack.Windows;

namespace TypeWhisper.Windows.Services;

public static class StartupService
{
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TypeWhisper";
    private const string MinimizedArgument = "--minimized";

    public static bool IsEnabled => HasStartupShortcut() || HasRegistryEntry();

    public static void Enable()
    {
        if (TryEnableStartupShortcut())
        {
            DeleteRegistryEntry();
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log("Enable failed: ProcessPath is null");
            return;
        }

        var value = $"\"{exePath}\" {MinimizedArgument}";
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
        key?.SetValue(AppName, value);
        Log($"Enabled auto-start via registry: {value}");
    }

    public static void Disable()
    {
        var removedShortcut = TryDisableStartupShortcut();
        var removedRegistry = DeleteRegistryEntry();
        Log($"Disabled auto-start (startup shortcut removed: {removedShortcut}, registry removed: {removedRegistry})");
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

#pragma warning disable CS0618 // Velopack's shortcut helper remains the supported API for Startup links.
    private static bool HasStartupShortcut()
    {
        var locator = GetVelopackLocator();
        if (locator is null || string.IsNullOrWhiteSpace(locator.ThisExeRelativePath))
            return false;

        try
        {
            var shortcuts = new Shortcuts(locator);
            return shortcuts.FindShortcuts(locator.ThisExeRelativePath, ShortcutLocation.Startup).Count > 0;
        }
        catch (Exception ex)
        {
            Log($"Startup shortcut detection failed: {ex.Message}");
            return false;
        }
    }

    private static bool HasRegistryEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            return key?.GetValue(AppName) is not null;
        }
        catch (Exception ex)
        {
            Log($"Registry detection failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryEnableStartupShortcut()
    {
        var locator = GetVelopackLocator();
        if (locator is null || string.IsNullOrWhiteSpace(locator.ThisExeRelativePath))
            return false;

        try
        {
            var shortcuts = new Shortcuts(locator);
            var existingShortcuts = shortcuts.FindShortcuts(locator.ThisExeRelativePath, ShortcutLocation.Startup);
            var iconPath = Environment.ProcessPath ?? locator.ProcessExePath ?? string.Empty;

            shortcuts.CreateShortcut(
                locator.ThisExeRelativePath,
                ShortcutLocation.Startup,
                updateOnly: existingShortcuts.Count > 0,
                programArguments: MinimizedArgument,
                icon: iconPath);

            Log($"Enabled auto-start via startup shortcut for '{locator.ThisExeRelativePath}'");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Startup shortcut enable failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryDisableStartupShortcut()
    {
        var locator = GetVelopackLocator();
        if (locator is null || string.IsNullOrWhiteSpace(locator.ThisExeRelativePath))
            return false;

        try
        {
            var shortcuts = new Shortcuts(locator);
            var existingShortcuts = shortcuts.FindShortcuts(locator.ThisExeRelativePath, ShortcutLocation.Startup);
            if (existingShortcuts.Count == 0)
                return false;

            shortcuts.DeleteShortcuts(locator.ThisExeRelativePath, ShortcutLocation.Startup);
            Log($"Removed startup shortcut for '{locator.ThisExeRelativePath}'");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Startup shortcut removal failed: {ex.Message}");
            return false;
        }
    }
#pragma warning restore CS0618

    private static bool DeleteRegistryEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
            var existed = key?.GetValue(AppName) is not null;
            key?.DeleteValue(AppName, throwOnMissingValue: false);
            if (existed)
                Log("Removed legacy registry auto-start entry");
            return existed;
        }
        catch (Exception ex)
        {
            Log($"Registry cleanup failed: {ex.Message}");
            return false;
        }
    }

    private static IVelopackLocator? GetVelopackLocator()
    {
        try
        {
            if (!VelopackLocator.IsCurrentSet)
                return null;

            return VelopackLocator.Current;
        }
        catch (Exception ex)
        {
            Log($"Velopack locator unavailable: {ex.Message}");
            return null;
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[StartupService] {message}");
        try
        {
            var logPath = Path.Combine(TypeWhisperEnvironment.LogsPath, "startup.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* ignore logging failures */ }
    }
}
