using Microsoft.Win32;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class WindowsStartupRegistration
{
    internal static StartupRegistration Create()
    {
        var backend = new RegistryBackend();
        if (WinUIProfile.IsTestProfile)
            return new(backend, StartupPublication.DevelopmentIdentity, null, "Windows startup is unavailable in isolated test profiles. No registry values are accessed.");
#if DEBUG
        try
        {
            var process = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
            var marker = Path.Combine(Path.GetDirectoryName(process)!, StartupPublication.ReceiptFileName);
            if (new FileInfo(marker).Length > 16_384) throw new InvalidDataException("Invalid publication receipt size.");
            var executable = StartupPublication.Validate(File.ReadAllText(marker), process);
            return new(backend, StartupPublication.DevelopmentIdentity, executable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(backend, StartupPublication.DevelopmentIdentity, null,
                "Startup is available only from the development launcher's published output. " + ex.Message);
        }
#else
        return new(backend, StartupPublication.DevelopmentIdentity, null,
            "Startup is unavailable until this build has an explicit installation identity.");
#endif
    }

    private sealed class RegistryBackend : IStartupRegistrationBackend
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public string? Read(string identity)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(identity, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null or string ? (string?)value : throw new InvalidDataException("The startup command has an unsupported registry type.");
        }
        public void Write(string identity, string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(identity, command, RegistryValueKind.String);
        }
        public void Delete(string identity)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(identity, throwOnMissingValue: false);
        }
    }
}
