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
            StartupRegistration.MigrateExecutable(backend, StartupPublication.DevelopmentIdentity,
                Path.Combine(Path.GetDirectoryName(executable)!, "TypeWhisper.WinUI.exe"), executable);
            return new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, executable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, null,
                "Startup is available only from the development launcher's published output. " + ex.Message);
        }
#else
        return CreateInstalled(backend);
#endif
    }

    /// <summary>Repairs an owned startup command left by the early 1.1 Daily executable rename before the next sign-in.
    /// Runs once per launch: a single registry read, never creating a registration or touching unrelated commands.</summary>
    internal static void MigrateInstalledCommand()
    {
#if !DEBUG && !TYPEWHISPER_STORE
        if (WinUIProfile.IsTestProfile) return;
        try
        {
            if (ResolveInstalled(Velopack.Locators.VelopackLocator.Current) is (var installation, var executable))
                installation.MigrateStartupCommand(new RegistryBackend(), executable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Startup registration could not be upgraded: {0}", ex); }
#endif
    }

    private static (ApplicationInstallation Installation, string Executable)? ResolveInstalled(Velopack.Locators.IVelopackLocator locator)
    {
        var installation = ApplicationInstallation.Resolve(locator.AppId);
        if (installation is null || locator.CurrentlyInstalledVersion is null || string.IsNullOrEmpty(locator.RootAppDir)) return null;
        var executable = Path.Combine(locator.RootAppDir, "current", installation.Executable);
        return File.Exists(executable) && string.Equals(Path.GetFullPath(executable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            ? (installation, executable) : null;
    }

    private static IStartupRegistration CreateInstalled(RegistryBackend backend)
    {
        var locator = Velopack.Locators.VelopackLocator.Current;
        if (ResolveInstalled(locator) is (var installation, var executable))
        {
            try { installation.MigrateStartupCommand(backend, executable); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return new StartupRegistration(backend, installation.PackageId, null, "Startup registration could not be upgraded. " + ex.Message); }
            var registration = new StartupRegistration(backend, installation.PackageId, executable);
            if (installation.PackageId != "TypeWhisper" || string.IsNullOrWhiteSpace(locator.ThisExeRelativePath)) return registration;
            // 1.0 used an owned Startup-folder shortcut. Keep it (including Windows' disabled state)
            // until the user explicitly changes startup, rather than creating a second registration.
#pragma warning disable CS0618
            var shortcuts = new Velopack.Windows.Shortcuts(locator);
            return new StartupRegistrationWithShortcut(registration,
                () => shortcuts.FindShortcuts(locator.ThisExeRelativePath, Velopack.Windows.ShortcutLocation.Startup).Count > 0,
                () => shortcuts.DeleteShortcuts(locator.ThisExeRelativePath, Velopack.Windows.ShortcutLocation.Startup));
#pragma warning restore CS0618
        }
        return new StartupRegistration(backend, "TypeWhisperDaily", null,
            "Windows startup is available after installing TypeWhisper.");
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
