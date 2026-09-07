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
    private readonly TypeWhisper.Presentation.ActivationInbox _activations = new();
    private bool _activationReady;

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
        var request = TypeWhisper.Presentation.ApplicationActivationRequest.Parse(Environment.GetCommandLineArgs().Skip(1),
            activation.Kind == ExtendedActivationKind.StartupTask);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!_mainInstance.IsCurrent)
        {
            try
            {
                if (request.ShowWindow) await _mainInstance.RedirectActivationToAsync(activation);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // This secondary instance owns no profile stores or UI. Always exit after logging;
                // do not offer recovery actions against the primary instance's profile.
                System.Diagnostics.Trace.TraceError("Activation redirection failed: {0}", ex);
            }
            finally { Exit(); }
            return;
        }

        _activations.Add(request);
        _mainInstance.Activated += (_, redirected) =>
        {
            var incoming = redirected.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs
                ? TypeWhisper.Presentation.ApplicationActivationRequest.ParseCommandLine(launchArgs.Arguments)
                : TypeWhisper.Presentation.ApplicationActivationRequest.Parse([], redirected.Kind == ExtendedActivationKind.StartupTask);
            if (!incoming.ShowWindow) return;
            _activations.Add(incoming);
            dispatcher.TryEnqueue(DrainActivations);
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
        if (request.ShowWindow) _window.ShowFromActivation();
        _tray = new TrayIconService(
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowFromActivation),
            () => _window.DispatcherQueue.TryEnqueue(_window.OpenSettings),
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowHistoryFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.OpenFilesFromTray),
            () => _window.DispatcherQueue.TryEnqueue(ExitFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.FinishDictationFromTray),
            () => _window.DispatcherQueue.TryEnqueue(async () => await _window.CancelProcessingAsync()));
        _window.DictationChanged += (status, recording) =>
        {
            _tray?.UpdateDictation(status, recording);
            _tray?.UpdateProcessing(_window.CanCancelProcessing);
        };
        var initialization = _window.InitializeDictationAsync();
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
        await initialization;
        _activationReady = true;
        DrainActivations();
    }

    private void DrainActivations()
    {
        if (_profileOperation is { } operation)
        {
            _activations.Close(); operation.Activate(); return;
        }
        if (_exiting) { _activations.Close(); return; }
        if (!_activationReady || _window is null) return;
        _activations.Dispatch(_window.HandleActivation, _window.ShowActivationFailure);
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
