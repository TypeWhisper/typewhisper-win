namespace TypeWhisper.WinUI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Before the worker dispatch, so worker and CrispASR processes inherit it.
        VulkanLayerPolicy.Apply();
        // A local speech engine runs in a copy of this executable, so a native crash ends only that copy.
        // Worker mode must start before installer hooks, WinUI and single-instance activation.
        if (TypeWhisper.PluginHost.TranscriptionWorkerServer.IsWorkerInvocation(args))
            Environment.Exit(TypeWhisper.PluginHost.TranscriptionWorkerServer.Run(args));
#if !DEBUG && !TYPEWHISPER_STORE
        // Installer callbacks must run before XAML, single-instance activation or profile access.
        // Candidate builds do not contact an update feed or automatically apply an update.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => WindowsStartupRegistration.Create().SetEnabledAsync(false).GetAwaiter().GetResult())
            .Run();
#endif
        AppLanguage.Apply();
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
