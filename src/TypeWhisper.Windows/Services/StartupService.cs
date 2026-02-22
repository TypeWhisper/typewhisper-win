using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

public static class StartupService
{
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TypeWhisper";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            return key?.GetValue(AppName) is not null;
        }
    }

    public static void Enable()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log("Enable failed: ProcessPath is null");
            return;
        }

        var value = $"\"{exePath}\" --minimized";
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
        key?.SetValue(AppName, value);
        Log($"Enabled auto-start: {value}");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
        Log("Disabled auto-start");
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
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
