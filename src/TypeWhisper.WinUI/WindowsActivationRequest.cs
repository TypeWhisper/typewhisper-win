using Microsoft.Windows.AppLifecycle;
using TypeWhisper.Presentation;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace TypeWhisper.WinUI;

internal static class WindowsActivationRequest
{
    internal static ApplicationActivationRequest Parse(AppActivationArguments activation, bool initial = false)
    {
        var startup = activation.Kind == ExtendedActivationKind.StartupTask;
        return activation.Data switch
        {
            ShareTargetActivatedEventArgs => ApplicationActivationRequest.Parse(["--files"]),
            IFileActivatedEventArgs files => ApplicationActivationRequest.Parse(
                new[] { "--transcribe-file" }.Concat(files.Files.Select(item => item is StorageFile file ? file.Path : string.Empty))),
            IProtocolActivatedEventArgs protocol => ApplicationActivationRequest.Parse([protocol.Uri.AbsoluteUri]),
            ICommandLineActivatedEventArgs command => ApplicationActivationRequest.ParseLaunchArguments(command.Operation.Arguments, Environment.ProcessPath, startup),
            ILaunchActivatedEventArgs launch => ApplicationActivationRequest.ParseLaunchArguments(launch.Arguments, Environment.ProcessPath, startup),
            _ => ApplicationActivationRequest.Parse(initial ? Environment.GetCommandLineArgs().Skip(1) : [], startup)
        };
    }
}
