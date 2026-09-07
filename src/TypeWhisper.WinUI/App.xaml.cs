using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace TypeWhisper.WinUI;

public partial class App : Application
{
    private const string InstanceKey = "TypeWhisper.WinUI.Dev.Primary";
    private MainWindow? _window;
    private AppInstance? _mainInstance;
    private TrayIconService? _tray;
    private ProfileOperationWindow? _profileOperation;

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
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var launch = TypeWhisper.Presentation.StartupLaunchPolicy.Evaluate(Environment.GetCommandLineArgs(),
            activation.Kind == ExtendedActivationKind.StartupTask);
        _mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!_mainInstance.IsCurrent)
        {
            if (launch.NotifyExisting) await _mainInstance.RedirectActivationToAsync(activation);
            Exit();
            return;
        }

        _mainInstance.Activated += (_, redirected) =>
        {
            var redirectedLaunch = redirected.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs
                ? TypeWhisper.Presentation.StartupLaunchPolicy.EvaluateCommandLine(launchArgs.Arguments)
                : TypeWhisper.Presentation.StartupLaunchPolicy.Evaluate([], redirected.Kind == ExtendedActivationKind.StartupTask);
            if (!redirectedLaunch.NotifyExisting) return;
            if (_profileOperation is { } operation)
            {
                operation.DispatcherQueue.TryEnqueue(operation.Activate);
                return;
            }
            var window = _window;
            if (window is not null)
                window.DispatcherQueue.TryEnqueue(window.ShowFromActivation);
        };
        try
        {
            var recovery = new TypeWhisper.Core.Services.PersistedProfileBackup(WinUIProfile.Root).RecoverPending();
            if (!recovery.CanOpenProfile)
            {
                ShowProfileFailure("A previous restore could not be recovered. Your files are preserved and the profile has not been opened. Close TypeWhisper before resolving this recovery error.", recovery.Error);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure("Profile recovery could not complete. Your profile has not been opened. Close TypeWhisper before resolving this recovery error.", ex.Message);
            return;
        }
        _window = new MainWindow();
        _window.RestoreProfile = RestoreProfileAsync;
        if (launch.ShowWindow) _window.ShowFromActivation();
        _tray = new TrayIconService(
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowFromActivation),
            () => _window.DispatcherQueue.TryEnqueue(_window.OpenSettings),
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowHistoryFromTray),
            () => _window.DispatcherQueue.TryEnqueue(() => { _window.ShowFromActivation(); _window.OpenFileTranscription(); }),
            () => _window.DispatcherQueue.TryEnqueue(ExitFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.FinishDictationFromTray),
            () => _window.DispatcherQueue.TryEnqueue(async () => await _window.CancelProcessingAsync()));
        _window.DictationChanged += (status, recording) =>
        {
            _tray?.UpdateDictation(status, recording);
            _tray?.UpdateProcessing(_window.CanCancelProcessing);
        };
        _ = _window.InitializeDictationAsync();
#if DEBUG
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_FIXTURE") == "1")
            _window.DispatcherQueue.TryEnqueue(_window.ShowHistoryFromTray);
        // Opt-in visual fixture: no capture, provider request, clipboard write or history entry.
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_REVIEW_FIXTURE") == "1")
            _window.DispatcherQueue.TryEnqueue(() => _window.ShowOutputReview(new(
                new TypeWhisper.Core.Models.TranscriptionRecord
                {
                    Id = "review-fixture", Timestamp = DateTime.UtcNow,
                    RawText = "Review window sample.",
                    FinalText = "Review window sample.\n\nDieser Text wurde nicht aufgenommen und nicht in der History gespeichert."
                }, false, true, "UI test sample. Nothing was recorded or pasted.")));
#endif
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

    private bool _exiting;
    private void ExitAfterProfileOperation()
    {
        _mainInstance?.UnregisterKey();
        Exit();
    }

    private void ShowProfileFailure(string message, string? details = null)
    {
        if (_profileOperation is null) _profileOperation = new(message, false, CloseProfileOperation);
        else _profileOperation.SetMessage(message, false);
        _profileOperation.SetDetails(details);
        _profileOperation.Activate();
    }

    private async Task RestoreProfileAsync(TypeWhisper.Core.Services.PersistedProfileBackup store,
        TypeWhisper.Core.Services.PersistedProfileBackupPreview preview)
    {
        if (_exiting || _window is null) return;
        _exiting = true;
        _profileOperation = new("Finishing active work before restoring your reviewed backup…", true, CloseProfileOperation);
        _profileOperation.Activate();
        try
        {
            _tray?.Dispose(); _tray = null;
            _window.FreezeForProfileRestore();
            await CompleteProfileRestoreAsync(store, preview);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure("Could not stop active work for restoration. No backup was applied.", ex.Message);
        }
    }

    private async Task CompleteProfileRestoreAsync(TypeWhisper.Core.Services.PersistedProfileBackup store,
        TypeWhisper.Core.Services.PersistedProfileBackupPreview preview)
    {
        _profileOperation!.SetMessage("Finishing active work before restoring your reviewed backup…", true);
        try
        {
            await _window!.ShutdownDictationAsync();
            // No dispatch or asynchronous continuation between publication and application exit.
            // All live runtime writers must have drained before Apply.
            var result = store.Apply(preview);
            if (result.Error is not null)
            {
                ShowProfileFailure(result.RecoveryRequired
                    ? "The restore needs recovery before your profile can open again. Close TypeWhisper and reopen it to finish recovery."
                    : "The backup was not applied. Close and reopen TypeWhisper, then review the backup again.", result.Error);
                return;
            }
            ExitAfterProfileOperation();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_window?.CanRetryRecorderShutdown == true)
            {
                ShowProfileFailure("The current recording could not be saved. Its audio is still held in memory. Retry saving before restoring. Closing the app discards that unsaved audio.");
                _profileOperation!.OfferSaveRetry(() => CompleteProfileRestoreAsync(store, preview));
            }
            else ShowProfileFailure("Restore did not complete. Close and reopen TypeWhisper before continuing.", ex.Message);
        }
    }

    private async void CloseProfileOperation()
    {
        _profileOperation?.SetMessage("Finishing shutdown…", true);
        try
        {
            // A failed recorder save may have left other already-started work draining.
            // Closing explicitly discards its unsaved audio, but never skips the other owners.
            if (_window is not null) await _window.DrainBeforeProfileExitAsync();
            ExitAfterProfileOperation();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure("Shutdown could not finish. Profile access remains stopped.", ex.Message);
        }
    }

    private async void ExitFromTray()
    {
        if (_exiting) return;
        _exiting = true;
        _tray?.SetShutdownState("Finishing shutdown…");
        try
        {
            if (_window is not null) await _window.ShutdownDictationAsync();
            _tray?.Dispose();
            _tray = null;
            _mainInstance?.UnregisterKey();
            Exit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Application shutdown failed: {0}", ex);
            _tray?.SetShutdownState("Shutdown failed. Work is stopped.");
            _window?.ShowShutdownFailure();
            if (_window?.CanRetryRecorderShutdown == true)
            {
                _exiting = false;
                _tray?.AllowShutdownRetry();
            }
        }
    }
}
