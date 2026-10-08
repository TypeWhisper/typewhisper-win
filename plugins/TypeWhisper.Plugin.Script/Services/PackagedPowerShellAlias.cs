using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.Plugin.Script;

internal static class PackagedPowerShellAlias
{
    internal const string Family = "Microsoft.PowerShell_8wekyb3d8bbwe";

    internal static bool Matches(IntPtr process, string alias, string image)
    {
        var aliasRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        if (!IsExpectedAlias(alias, aliasRoot)) return false;

        // Windows Store execution aliases redirect to a versioned package path. Verify the process's
        // OS-assigned package identity AND its executable inside that package before sending stdin.
        var family = new StringBuilder(256);
        var length = family.Capacity;
        if (GetPackageFamilyName(process, ref length, family) != 0 || family.ToString() != Family) return false;
        var fullName = new StringBuilder(256);
        length = fullName.Capacity;
        if (GetPackageFullName(process, ref length, fullName) != 0) return false;
        var directory = new StringBuilder(32768);
        length = directory.Capacity;
        if (GetPackagePathByFullName(fullName.ToString(), ref length, directory) != 0) return false;
        return SamePath(image, Path.Combine(directory.ToString(), "pwsh.exe"));
    }

    internal static bool IsExpectedAlias(string path, string aliasRoot) =>
        SamePath(path, Path.Combine(aliasRoot, "pwsh.exe"))
        || SamePath(path, Path.Combine(aliasRoot, Family, "pwsh.exe"));

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(IntPtr process, ref int length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFullName(IntPtr process, ref int length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string fullName, ref int length, StringBuilder path);
}
