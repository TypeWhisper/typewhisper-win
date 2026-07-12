namespace TypeWhisper.PluginSystem.Tests;

public sealed class TrayIntegrationTests
{
    [Fact]
    public void App_RemainsRunningInTrayUntilExplicitExit()
    {
        var appXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml");
        var appCode = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");

        Assert.Contains("ShutdownMode=\"OnExplicitShutdown\"", appXaml);
        Assert.Contains("_trayIcon.ExitRequested += (_, _) => Shutdown();", appCode);
    }

    [Fact]
    public void App_DispatchesTrayViewModelActionsToUiThread()
    {
        var code = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");

        Assert.Contains("void RunTrayActionOnUiThread(Action action)", code);
        Assert.Contains("_trayIcon.ShowSettingsRequested += (_, _) => RunTrayActionOnUiThread(() => ShowSettingsWindow());", code);
        Assert.Contains("_trayIcon.ShowFileTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(() => ShowSettingsWindow(SettingsRoute.FileTranscription, presentFileImporter: true));", code);
        Assert.Contains("_trayIcon.ShowRecentTranscriptionsRequested += (_, _) => RunTrayActionOnUiThread(() =>", code);
        Assert.Contains("_trayIcon.ReadBackLastTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(() =>", code);
        Assert.Contains("_trayIcon.ToggleRecorderRequested += (_, _) => RunTrayActionOnUiThread(() =>", code);
        Assert.Contains("GetRequiredService<AudioRecorderViewModel>().ToggleRecordingCommand.Execute(null)", code);
    }

    [Fact]
    public void App_DispatchesAsyncTrayActionsToUiThread()
    {
        var code = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");

        Assert.Contains("void RunTrayActionOnUiThread(Func<Task> action)", code);
        Assert.Contains("_trayIcon.CopyLastTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(async () =>", code);
        Assert.Contains("_trayIcon.UpdateCheckRequested += (_, _) => RunTrayActionOnUiThread(async () =>", code);
        Assert.Contains("catch (Exception ex) when (IsNonFatalTrayActionException(ex))", code);
        Assert.Contains("IsNonFatalTrayActionException", code);
    }
}
