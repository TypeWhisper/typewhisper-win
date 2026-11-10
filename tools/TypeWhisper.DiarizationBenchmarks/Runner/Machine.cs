using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TypeWhisper.DiarizationBenchmarks;

/// <summary>The hypothesis file's <c>machine</c> block, read from the registry and the OS.</summary>
[SupportedOSPlatform("windows")]
internal static partial class Machine
{
    public static JsonObject Describe(string? accelerator)
    {
        var machine = new JsonObject
        {
            ["model"] = Model(),
            ["chip"] = Read(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "unknown",
            ["memoryGb"] = MemoryGb(),
            ["os"] = OsName(),
        };
        if (accelerator is not null)
        {
            machine["accelerator"] = accelerator;
        }

        return machine;
    }

    /// <summary>The discrete GPU and its driver, as nvidia-smi reports them.</summary>
    public static string? Gpu()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(info);
            var line = process?.StandardOutput.ReadLine();
            process?.WaitForExit();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            return parts.Length == 3 ? $"{parts[0]} ({parts[1]}, driver {parts[2]})" : line.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static string OsName()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = Read(key, "CurrentBuild") ?? Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ubr = Registry.LocalMachine.OpenSubKey(key)?.GetValue("UBR")?.ToString();
        var edition = Read(key, "EditionID") ?? "";
        var version = Read(key, "DisplayVersion");
        // ProductName still says "Windows 10" on Windows 11; the build number decides.
        var name = int.TryParse(build, out var number) && number >= 22000 ? "Windows 11" : "Windows 10";
        var editionName = edition switch
        {
            "Professional" => " Pro",
            "Core" => " Home",
            "Enterprise" => " Enterprise",
            "" => "",
            _ => " " + edition,
        };
        return $"{name}{editionName} {(version is null ? "" : version + " ")}build {build}{(ubr is null ? "" : "." + ubr)}".Trim();
    }

    private static string Model()
    {
        const string bios = @"HARDWARE\DESCRIPTION\System\BIOS";
        var manufacturer = Read(bios, "SystemManufacturer")?.Trim();
        var product = Read(bios, "SystemProductName")?.Trim();
        static bool Placeholder(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Contains("To be filled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            || value.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Default string", StringComparison.OrdinalIgnoreCase);
        if (!Placeholder(product))
        {
            return Placeholder(manufacturer) ? product! : $"{manufacturer} {product}";
        }

        // A self-built desktop has no system model; the mainboard identifies it.
        var boardMaker = Read(bios, "BaseBoardManufacturer")?.Trim();
        var board = Read(bios, "BaseBoardProduct")?.Trim();
        return Placeholder(board) ? "Desktop PC" : $"Desktop PC ({boardMaker} {board})";
    }

    private static int MemoryGb()
    {
        // Installed memory; GlobalMemoryStatusEx reports less, after firmware and GPU reservations.
        if (GetPhysicallyInstalledSystemMemory(out var kilobytes))
        {
            return (int)Math.Round(kilobytes / (1024.0 * 1024));
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (int)Math.Round(status.TotalPhys / (1024.0 * 1024 * 1024)) : 0;
    }

    private static string? Read(string key, string name) => Registry.LocalMachine.OpenSubKey(key)?.GetValue(name)?.ToString();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
