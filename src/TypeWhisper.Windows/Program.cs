using System.Diagnostics;
using System.IO;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.Native;
using TypeWhisper.Core;
#if TYPEWHISPER_STORE
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
#else
using Velopack;
#endif

namespace TypeWhisper.Windows;

/// <summary>
/// Provides program behavior.
/// </summary>
public static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static SingleInstanceActivationSignal? _singleInstanceActivationSignal;
    private static IReadOnlyList<string>? _restartArgs;
    private static string CallbackInboxPath => Path.Combine(TypeWhisperEnvironment.DataPath, "protocol-callback.txt");

    /// <summary>
    /// Gets the current debug UI automation launch options.
    /// </summary>
    internal static UiAutomationLaunchOptions UiAutomation { get; private set; } = UiAutomationLaunchOptions.Disabled;

    /// <summary>
    /// Gets or sets the start minimized value.
    /// </summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>
    /// Performs request restart.
    /// </summary>
    public static void RequestRestart(params string[] args)
    {
        _restartArgs = args.ToArray();
    }

    /// <summary>
    /// Performs main.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        if (NativeModelProbe.TryRun(args, out var probeExitCode))
        {
            Environment.ExitCode = probeExitCode;
            return;
        }

        if (!UiAutomationLaunchOptions.TryParse(args, out var automationOptions, out var automationError))
        {
            Debug.WriteLine(automationError);
            Environment.ExitCode = 2;
            return;
        }

        UiAutomation = automationOptions;
        if (UiAutomation.IsEnabled)
        {
            Environment.SetEnvironmentVariable(
                TypeWhisperEnvironment.UiAutomationDataRootEnvironmentVariable,
                UiAutomation.DataRoot);
        }

        var isStartupActivation = false;
#if TYPEWHISPER_STORE
        try
        {
            isStartupActivation = AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Store activation detection failed: {ex.Message}");
        }
#else
        if (!UiAutomation.IsEnabled)
        {
            VelopackApp.Build()
                .SetAutoApplyOnStartup(!IsPortableLayout(AppContext.BaseDirectory))
                .OnFirstRun((_) => StartupService.Enable())
                .OnBeforeUninstallFastCallback((v) =>
                {
                    UninstallUserDataProtector.ProtectLegacyAudioDirectory();
                })
                .OnAfterUpdateFastCallback((v) =>
                {
                    if (StartupService.IsEnabled)
                        StartupService.Enable();
                })
                .Run();
        }
#endif

        StartMinimized = isStartupActivation
            || args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var callbackArg = args.FirstOrDefault(SupporterDiscordService.CanHandleCallbackUri);

        // Single instance check
        var synchronizationSuffix = UiAutomation.IsEnabled ? $"-UiAutomation-{UiAutomation.InstanceId}" : string.Empty;
        using var activationSignal = SingleInstanceActivationSignal.OpenOrCreate(
            $"TypeWhisper-SingleInstance-Activation{synchronizationSuffix}");
        _singleInstanceMutex = new Mutex(
            true,
            $"TypeWhisper-SingleInstance{synchronizationSuffix}",
            out var createdNew);
        if (!createdNew)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(callbackArg))
                {
                    Directory.CreateDirectory(TypeWhisperEnvironment.DataPath);
                    File.WriteAllText(CallbackInboxPath, callbackArg);
                }

                if (ShouldNotifyRunningInstance(StartMinimized, callbackArg))
                {
                    AllowRunningInstanceToSetForegroundWindow();
                    activationSignal.Notify();
                }
            }
            finally
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            // Another instance is already running
            return;
        }

        _singleInstanceActivationSignal = activationSignal;

        try
        {
            Loc.Instance.CurrentLanguage = UiAutomation.IsEnabled
                ? UiAutomation.Language
                : Loc.Instance.DetectSystemLanguage();

            try
            {
                if (!UiAutomation.IsEnabled)
                    UserDataMigrationService.MigrateLegacyDataIfNeeded();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show(
                    Loc.Instance.GetString("App.UserDataMigrationErrorFormat", ex.Message),
                    "TypeWhisper",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return;
            }

            TypeWhisperEnvironment.EnsureDirectories();
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        finally
        {
            var restartArgs = _restartArgs;
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _singleInstanceActivationSignal = null;

            if (restartArgs is not null)
                StartRestartProcess(restartArgs);
        }
    }

    internal static RegisteredWaitHandle ListenForActivationRequests(Action callback)
    {
        var signal = _singleInstanceActivationSignal
            ?? throw new InvalidOperationException("The single-instance activation signal is not initialized.");
        return signal.Listen(callback);
    }

    internal static bool ShouldNotifyRunningInstance(bool startMinimized, string? callbackArg) =>
        !startMinimized && string.IsNullOrWhiteSpace(callbackArg);

    private static void AllowRunningInstanceToSetForegroundWindow()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var candidate in Process.GetProcessesByName(current.ProcessName))
        {
            using (candidate)
            {
                try
                {
                    if (candidate.Id == current.Id || candidate.SessionId != current.SessionId)
                        continue;

                    NativeMethods.AllowSetForegroundWindow((uint)candidate.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Debug.WriteLine($"Could not grant foreground access to TypeWhisper process {candidate.Id}: {ex.Message}");
                }
            }
        }
    }

    internal static bool IsPortableLayout(string appBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(appBaseDirectory))
            return false;

        var contentDirectory = new DirectoryInfo(Path.GetFullPath(appBaseDirectory));
        return contentDirectory.Parent is { } packageRoot
            && File.Exists(Path.Join(packageRoot.FullName, ".portable"));
    }

    private static void StartRestartProcess(IReadOnlyList<string> args)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restart TypeWhisper: {ex.Message}");
        }
    }
}
