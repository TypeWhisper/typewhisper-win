using TypeWhisper.Windows.Services;
using Velopack;

namespace TypeWhisper.Windows;

public static class Program
{
    private static Mutex? _singleInstanceMutex;

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

        // Single instance check
        _singleInstanceMutex = new Mutex(true, "TypeWhisper-SingleInstance", out var createdNew);
        if (!createdNew)
        {
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
