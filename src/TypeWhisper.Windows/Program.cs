using System.IO;
using TypeWhisper.Windows.Services;
using Velopack;
using TypeWhisper.Core;

namespace TypeWhisper.Windows;

public static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static readonly string CallbackInboxPath = Path.Combine(TypeWhisperEnvironment.DataPath, "protocol-callback.txt");

    public static bool StartMinimized { get; private set; }

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
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }
}
