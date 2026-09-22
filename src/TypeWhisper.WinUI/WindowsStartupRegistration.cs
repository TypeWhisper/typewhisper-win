using Microsoft.Win32;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class WindowsStartupRegistration
{
    internal static IStartupRegistration Create()
    {
        var backend = new RegistryBackend();
        if (WinUIProfile.IsTestProfile)
            return new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, null, "Windows startup is unavailable in isolated test profiles. No startup registrations are accessed.");
#if TYPEWHISPER_STORE
        const string taskId = "TypeWhisperStartup";
        return new PackagedStartupRegistration(
            async () => (await global::Windows.ApplicationModel.StartupTask.GetAsync(taskId)).State switch
            {
                global::Windows.ApplicationModel.StartupTaskState.Disabled => PackagedStartupState.Disabled,
                global::Windows.ApplicationModel.StartupTaskState.DisabledByUser => PackagedStartupState.DisabledByUser,
                global::Windows.ApplicationModel.StartupTaskState.Enabled => PackagedStartupState.Enabled,
                global::Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => PackagedStartupState.DisabledByPolicy,
                global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => PackagedStartupState.EnabledByPolicy,
                _ => (PackagedStartupState)(-1)
            },
            async () => { var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(taskId); await task.RequestEnableAsync(); },
            async () => { var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(taskId); task.Disable(); });
#elif DEBUG
        try
        {
            var process = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
            var marker = Path.Combine(Path.GetDirectoryName(process)!, StartupPublication.ReceiptFileName);
            if (new FileInfo(marker).Length > 16_384) throw new InvalidDataException("Invalid publication receipt size.");
            var executable = StartupPublication.Validate(File.ReadAllText(marker), process);
            return new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, executable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, null,
                "Startup is available only from the development launcher's published output. " + ex.Message);
        }
#else
        var locator = Velopack.Locators.VelopackLocator.Current;
        if (locator.AppId == "TypeWhisperDaily" && locator.CurrentlyInstalledVersion is not null &&
            !string.IsNullOrEmpty(locator.RootAppDir))
        {
            var executable = Path.Combine(locator.RootAppDir, "current", "TypeWhisper.WinUI.exe");
            if (File.Exists(executable) && string.Equals(Path.GetFullPath(executable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return new StartupRegistration(backend, "TypeWhisperDaily", executable);
        }
        return new StartupRegistration(backend, "TypeWhisperDaily", null,
            "Windows startup is available after installing TypeWhisper Daily.");
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
