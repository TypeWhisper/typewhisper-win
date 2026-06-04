using System.IO;
using System.Diagnostics;
using TypeWhisper.Windows.Services;
using Velopack;
using TypeWhisper.Core;

namespace TypeWhisper.Windows;

/// <summary>
/// Provides program behavior.
/// </summary>
public static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static IReadOnlyList<string>? _restartArgs;
    private static readonly string CallbackInboxPath = Path.Combine(TypeWhisperEnvironment.DataPath, "protocol-callback.txt");

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
        VelopackApp.Build()
            .OnAfterUpdateFastCallback((v) =>
            {
                if (StartupService.IsEnabled)
                    StartupService.Enable();
            })
            .Run();

        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        TypeWhisperEnvironment.EnsureDirectories();
        var callbackArg = args.FirstOrDefault(SupporterDiscordService.CanHandleCallbackUri);

        // Single instance check
        _singleInstanceMutex = new Mutex(true, "TypeWhisper-SingleInstance", out var createdNew);
        if (!createdNew)
        {
            if (!string.IsNullOrWhiteSpace(callbackArg))
                File.WriteAllText(CallbackInboxPath, callbackArg);

            // Another instance is already running
            return;
        }

        try
        {
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

            if (restartArgs is not null)
                StartRestartProcess(restartArgs);
        }
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
