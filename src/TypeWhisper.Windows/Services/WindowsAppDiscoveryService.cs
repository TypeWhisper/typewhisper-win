using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.Services;

public enum WindowsAppDiscoverySource
{
    Running,
    Installed,
    History
}

public sealed record WindowsAppDescriptor(
    string ProcessName,
    string DisplayName,
    string? ExecutablePath,
    WindowsAppDiscoverySource Source,
    ImageSource? Icon);

public sealed class WindowsAppDiscoveryService
{
    private static readonly string OwnProcessName = Process.GetCurrentProcess().ProcessName;
    private readonly IHistoryService _history;
    private IReadOnlyList<WindowsAppDescriptor>? _cachedApps;
    private DateTime _lastScanUtc;

    public WindowsAppDiscoveryService(IHistoryService history)
    {
        _history = history;
    }

    public IReadOnlyList<WindowsAppDescriptor> GetApps(bool forceRefresh = false)
    {
        if (!forceRefresh
            && _cachedApps is not null
            && DateTime.UtcNow - _lastScanUtc < TimeSpan.FromMinutes(2))
        {
            return _cachedApps;
        }

        var apps = new Dictionary<string, WindowsAppDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in GetRunningApps())
            Merge(apps, app);

        foreach (var app in GetInstalledApps())
            Merge(apps, app);

        foreach (var processName in _history.GetDistinctApps())
        {
            if (IsUsableProcessName(processName))
            {
                Merge(apps, new WindowsAppDescriptor(
                    processName,
                    ToDisplayName(processName),
                    null,
                    WindowsAppDiscoverySource.History,
                    null));
            }
        }

        _cachedApps = apps.Values
            .OrderBy(app => SourceRank(app.Source))
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _lastScanUtc = DateTime.UtcNow;

        return _cachedApps;
    }

    private static void Merge(Dictionary<string, WindowsAppDescriptor> apps, WindowsAppDescriptor candidate)
    {
        if (!IsUsableProcessName(candidate.ProcessName))
            return;

        if (!apps.TryGetValue(candidate.ProcessName, out var existing))
        {
            apps[candidate.ProcessName] = candidate;
            return;
        }

        var candidateRank = SourceRank(candidate.Source);
        var existingRank = SourceRank(existing.Source);
        if (candidateRank < existingRank
            || (existing.Icon is null && candidate.Icon is not null)
            || (string.Equals(existing.DisplayName, existing.ProcessName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.DisplayName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            apps[candidate.ProcessName] = candidate;
        }
    }

    private static IEnumerable<WindowsAppDescriptor> GetRunningApps()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? processName;
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero || process.Id == Environment.ProcessId)
                        continue;

                    processName = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (!IsUsableProcessName(processName))
                    continue;

                var executablePath = TryGetMainModulePath(process);
                yield return new WindowsAppDescriptor(
                    processName,
                    DisplayNameFromExecutable(executablePath, processName),
                    executablePath,
                    WindowsAppDiscoverySource.Running,
                    LoadIcon(executablePath));
            }
        }
    }

    private static IEnumerable<WindowsAppDescriptor> GetInstalledApps()
    {
        foreach (var registryRoot in RegistryRoots())
        {
            using var root = RegistryKey.OpenBaseKey(registryRoot.Hive, registryRoot.View);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
                continue;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var subKey = uninstall.OpenSubKey(subKeyName);
                if (subKey is null)
                    continue;

                var displayName = (subKey.GetValue("DisplayName") as string)?.Trim();
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;

                var executablePath = ResolveExecutablePath(
                    subKey.GetValue("DisplayIcon") as string,
                    subKey.GetValue("InstallLocation") as string);

                if (string.IsNullOrWhiteSpace(executablePath))
                    continue;

                var processName = Path.GetFileNameWithoutExtension(executablePath);
                if (!IsUsableProcessName(processName))
                    continue;

                yield return new WindowsAppDescriptor(
                    processName,
                    displayName,
                    executablePath,
                    WindowsAppDiscoverySource.Installed,
                    LoadIcon(executablePath));
            }
        }
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View)> RegistryRoots()
    {
        yield return (RegistryHive.CurrentUser, RegistryView.Default);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
    }

    private static string? ResolveExecutablePath(string? displayIcon, string? installLocation)
    {
        var iconPath = NormalizeExecutablePath(displayIcon);
        if (IsExistingExecutable(iconPath))
            return iconPath;

        var location = Environment.ExpandEnvironmentVariables((installLocation ?? "").Trim().Trim('"'));
        if (Directory.Exists(location))
        {
            var candidate = Directory.EnumerateFiles(location, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => !Path.GetFileName(path).Contains("unins", StringComparison.OrdinalIgnoreCase));
            if (IsExistingExecutable(candidate))
                return candidate;
        }

        return null;
    }

    private static string? NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote > 1)
                expanded = expanded[1..closingQuote];
        }
        else
        {
            var exeIndex = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
                expanded = expanded[..(exeIndex + 4)];
            else if (expanded.Contains(','))
                expanded = expanded.Split(',')[0];
        }

        return expanded.Trim().Trim('"');
    }

    private static string? TryGetMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadIcon(string? executablePath)
    {
        if (!IsExistingExecutable(executablePath))
            return null;

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath!);
            if (icon is null)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static string DisplayNameFromExecutable(string? executablePath, string processName)
    {
        if (IsExistingExecutable(executablePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(executablePath!);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    return info.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(info.ProductName))
                    return info.ProductName.Trim();
            }
            catch
            {
                // Fall through to process-name display.
            }
        }

        return ToDisplayName(processName);
    }

    private static string ToDisplayName(string value)
    {
        var name = value.Replace('-', ' ').Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(name)
            ? value
            : string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static bool IsExistingExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    private static int SourceRank(WindowsAppDiscoverySource source) => source switch
    {
        WindowsAppDiscoverySource.Running => 0,
        WindowsAppDiscoverySource.Installed => 1,
        WindowsAppDiscoverySource.History => 2,
        _ => 3
    };

    private static bool IsUsableProcessName(string? processName) =>
        !string.IsNullOrWhiteSpace(processName)
        && !string.Equals(processName, OwnProcessName, StringComparison.OrdinalIgnoreCase);
}
