namespace TypeWhisper.WinUI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
#if !DEBUG
        // Installer callbacks must run before XAML, single-instance activation or profile access.
        // Candidate builds do not contact an update feed or automatically apply an update.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => WindowsStartupRegistration.Create().SetEnabled(false))
            .Run();
#endif
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
