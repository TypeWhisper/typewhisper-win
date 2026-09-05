using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace TypeWhisper.WinUIPrototype;

public partial class App : Application
{
    private const string InstanceKey = "TypeWhisper.WinUIPrototype.Primary";
    private MainWindow? _window;
    private AppInstance? _mainInstance;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "TypeWhisper-WinUI-Prototype-errors.log"),
                    $"{DateTimeOffset.Now:O} {args.Exception}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            args.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!_mainInstance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            await _mainInstance.RedirectActivationToAsync(activation);
            Exit();
            return;
        }

        _mainInstance.Activated += (_, _) =>
        {
            var window = _window;
            if (window is not null)
                window.DispatcherQueue.TryEnqueue(window.ShowFromActivation);
        };
        _window = new MainWindow();
        _window.Activate();
        if (Environment.GetCommandLineArgs().Contains("--account"))
            _window.DispatcherQueue.TryEnqueue(_window.OpenAccount);
        else if (Environment.GetCommandLineArgs().Contains("--sync-backup"))
            _window.DispatcherQueue.TryEnqueue(_window.OpenSyncBackup);
        else if (Environment.GetCommandLineArgs().Contains("--dashboard"))
            _window.DispatcherQueue.TryEnqueue(() => _window.OpenDashboard());
        else if (Environment.GetCommandLineArgs().Contains("--statistics"))
            _window.DispatcherQueue.TryEnqueue(() => _window.OpenDashboard(true));
        else if (Environment.GetCommandLineArgs().Contains("--dictionary"))
            _window.DispatcherQueue.TryEnqueue(() => _window.OpenLexicon());
        else if (Environment.GetCommandLineArgs().Contains("--snippets"))
            _window.DispatcherQueue.TryEnqueue(() => _window.OpenLexicon(true));
        else if (Environment.GetCommandLineArgs().Contains("--files"))
            _window.DispatcherQueue.TryEnqueue(_window.OpenFileTranscription);
        else if (Environment.GetCommandLineArgs().Contains("--setup"))
            _window.DispatcherQueue.TryEnqueue(_window.OpenSetup);
        else if (Environment.GetCommandLineArgs().Contains("--compare-selects"))
            _window.DispatcherQueue.TryEnqueue(_window.OpenSelectComparison);
        else if (Environment.GetCommandLineArgs().Contains("--settings"))
            _window.DispatcherQueue.TryEnqueue(_window.OpenSettings);
    }
}
