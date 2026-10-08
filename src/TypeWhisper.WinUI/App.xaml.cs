using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TypeWhisper.Core.Services.UserData;

namespace TypeWhisper.WinUI;

public partial class App : Application
{
    private MainWindow? _window;
    private AppInstance? _mainInstance;
    private TrayIconService? _tray;
    private ProfileOperationWindow? _profileOperation;
    private readonly TypeWhisper.Presentation.ActivationInbox _activations = new();
    private bool _activationReady;
    private readonly TaskCompletionSource<bool> _shareStartupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public App()
    {
        InitializeComponent();
        AppTheme.Apply(this);
        UnhandledException += (_, args) =>
        {
            AppDiagnostics.WriteFailure("app.unhandled-exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // The process ends after this handler; the line must reach the file before that.
            if (args.ExceptionObject is Exception error) AppDiagnostics.WriteFailureNow("app.crash", error);
        };
        // The scheduler wraps the fault; its type and stack are on the inner exception.
        TaskScheduler.UnobservedTaskException += (_, args) => AppDiagnostics.WriteFailure("task.unobserved-exception",
            args.Exception.InnerExceptions is [var single] ? single : args.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        if (WinUIProfile.IsTestProfile && Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_TRAY_LAYOUT_PROBE") == "1")
        {
            await TrayMenuLayoutProbe.RunAsync();
            Exit();
            return;
        }
#endif
        try { await LaunchAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure(Loc.T("TypeWhisper could not finish starting. Close and reopen the app before trying again."), ex.Message);
        }
    }

    private async Task LaunchAsync()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var share = activation.Data as global::Windows.ApplicationModel.Activation.ShareTargetActivatedEventArgs;
        var request = WindowsActivationRequest.Parse(activation, initial: true);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _mainInstance = AppInstance.FindOrRegisterForKey(WinUIProfile.InstanceKey);
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
                if (share is not null)
                    TypeWhisper.Presentation.SharedFileActivation.Reject(new WindowsSharedFileOperation(share.ShareOperation),
                        Loc.T("TypeWhisper could not hand the shared files to the running app. Please share them again."));
            }
            finally { Exit(); }
            return;
        }
        // Updates restart into the renamed executable; repair the owned Run command before the next sign-in.
        WindowsStartupRegistration.MigrateInstalledCommand();
        _ = Task.Run(() => StableImportCopy.CleanupAbandonedCopies());

        _mainInstance.Activated += (_, redirected) =>
        {
            if (redirected.Data is global::Windows.ApplicationModel.Activation.ShareTargetActivatedEventArgs shared)
            {
                var operation = new WindowsSharedFileOperation(shared.ShareOperation);
                if (!dispatcher.TryEnqueue(async () =>
                {
                    await TypeWhisper.Presentation.SharedFileActivation.ReceiveAsync(operation, _activations, CanReceiveSharedActivation, _shareStartupReady.Task);
                    DrainActivations();
                }))
                    TypeWhisper.Presentation.SharedFileActivation.Reject(operation, Loc.T("TypeWhisper is shutting down. Reopen the app and share the files again."));
                return;
            }
            var incoming = WindowsActivationRequest.Parse(redirected);
            if (!incoming.ShowWindow) return;
            _activations.Add(incoming);
            dispatcher.TryEnqueue(DrainActivations);
        };
        // Report reception promptly, but do not retrieve or acknowledge files until the
        // profile and host have initialized. Redirected shares use the same readiness task.
        var initialShare = share is null ? Task.CompletedTask :
            TypeWhisper.Presentation.SharedFileActivation.ReceiveAsync(new WindowsSharedFileOperation(share.ShareOperation),
                _activations, CanReceiveSharedActivation, _shareStartupReady.Task);
        await OpenProfileAsync(request, initialShare, skipLegacyImport: false);
    }

    // Also re-entered from the upgrade failure window (Retry / Start with a new profile).
    private async Task OpenProfileAsync(TypeWhisper.Presentation.ApplicationActivationRequest request, Task initialShare, bool skipLegacyImport)
    {
        try
        {
            if (!await FinishPendingDataDeletionAsync(request, initialShare, skipLegacyImport)) return;
#if !DEBUG
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // #318: a failed 1.0.4 migration can leave an empty TypeWhisper-UserData beside the real profile.
            if (WinUIProfile.UsesLegacyData && !Directory.Exists(WinUIProfile.Root) && TypeWhisper.Core.Services.LegacyDailyProfileMigration.SelectSource(
                    Path.Combine(localData, "TypeWhisper-UserData"), Path.Combine(localData, "TypeWhisper")) is { } legacy &&
                !await ImportLegacyProfileAsync(legacy, request, initialShare, skipLegacyImport))
                return;
#endif
            var recovery = new TypeWhisper.Core.Services.PersistedProfileBackup(WinUIProfile.Root).RecoverPending();
            if (!recovery.CanOpenProfile)
            {
                ShowProfileFailure(Loc.T("A previous restore could not be recovered. Your files are preserved and the profile has not been opened. Close TypeWhisper before resolving this recovery error."), recovery.Error);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure(Loc.T("Profile recovery could not complete. Your profile has not been opened. Close TypeWhisper before resolving this recovery error."), ex.Message);
            return;
        }
        await StartWithProfileAsync(request, initialShare);
    }

    // A failed import never dead-ends: retry, or publish an empty profile whose receipt records the skip.
    // Neither path writes to the previous profile.
    private async Task<bool> ImportLegacyProfileAsync(string legacy,
        TypeWhisper.Presentation.ApplicationActivationRequest request, Task initialShare, bool skip)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var message = skip ? Loc.T("Creating a new TypeWhisper profile. Your previous data stays unchanged…")
            : Loc.T("Upgrading your TypeWhisper profile. Your previous data will be preserved…");
        if (_profileOperation is null)
        {
            _profileOperation = new ProfileOperationWindow(message, true, Exit, Loc.T("Profile upgrade"));
            _profileOperation.Activate();
        }
        else { _profileOperation.SetMessage(message, true); _profileOperation.SetDetails(null); }
        try
        {
            if (skip) TypeWhisper.Core.Services.LegacyDailyProfileMigration.SkipImport(WinUIProfile.Root);
            else await TypeWhisper.Core.Services.LegacyDailyProfileMigration.ImportAsync(legacy, WinUIProfile.Root,
                prepareProfile: (source, stage, ct) => Task.Run(() => LegacyWindowsProfileMigration.PrepareAsync(
                    source, stage, LocalCtcVocabulary.HostVersion,
                    update => dispatcher.TryEnqueue(() => _profileOperation?.SetMessage(update, true)), ct)));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Retry or a new profile can still finish startup; keep shares and activations pending until then.
            ShowProfileFailure(TypeWhisper.Core.Services.LegacyDailyProfileMigration.DescribeFailure(ex), ex.Message,
                keepStartupPending: true);
            _profileOperation!.OfferActions(
                Loc.T("Retry"), () => ContinueLaunchAsync(() => OpenProfileAsync(request, initialShare, skipLegacyImport: false)),
                Loc.T("Start with a new profile"), () => ContinueLaunchAsync(() => OpenProfileAsync(request, initialShare, skipLegacyImport: true)));
            return false;
        }
    }

    private async Task ContinueLaunchAsync(Func<Task> launch)
    {
        try { await launch(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure(Loc.T("TypeWhisper could not finish starting. Close and reopen the app before trying again."), ex.Message);
        }
    }

    private async Task StartWithProfileAsync(TypeWhisper.Presentation.ApplicationActivationRequest request, Task initialShare)
    {
        AppDiagnostics.Start();
        // Retries and splits of cloud requests are decided in the plugin host, which has no log of its own.
        TypeWhisper.PluginHost.PluginHostDiagnostics.Sink = (stage, error) => AppDiagnostics.Write(stage, error);
        var setup = new TypeWhisper.Presentation.SetupPreferencesStore(WinUIProfile.DataPath("setup.json"));
        var presentation = TypeWhisper.Presentation.StartupPresentationPolicy.Resolve(request, setup.Current.Completed);
        if (presentation == TypeWhisper.Presentation.StartupPresentation.RequestedDestination) _activations.Add(request);
        _window = new MainWindow();
        // Keep a window alive throughout startup; closing the last window can end the XAML application.
        _profileOperation?.Dismiss();
        _profileOperation = null;
        _window.RestartApplicationAsync = () => ExitOrRestartAsync(restart: true);
        _window.InstallApplicationUpdateAsync = apply => ExitOrRestartAsync(restart: true, applyUpdate: apply);
        _window.RestoreProfile = (store, preview) => RestoreProfileAsync(store, preview);
        _window.RestoreApiProfile = (store, preview) => RestoreProfileAsync(store, preview, true);
        _window.DeleteAllData = DeleteAllDataAsync;
        _tray = new TrayIconService(
            () => _window.DispatcherQueue.TryEnqueue(_window.ToggleRecorderFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowRecentTranscriptionsFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.OpenSettings),
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowHistoryFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.OpenFilesFromTray),
            () => _window.DispatcherQueue.TryEnqueue(ExitFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.FinishDictationFromTray),
            () => _window.DispatcherQueue.TryEnqueue(async () => await _window.CancelProcessingAsync()),
            () => _window.DispatcherQueue.TryEnqueue(_window.ToggleDictationHotkeyPause),
            () => _window.DispatcherQueue.TryEnqueue(_window.OpenRecoveryFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowApplicationUpdates),
            () => _window.DispatcherQueue.TryEnqueue(_window.PasteLastTranscriptionFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.CopyLastTranscriptionFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.ReadLastTranscriptionFromTray),
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowDiagnosticsFromTray));
        _window.TrayMenuHandle = _tray.WindowHandle;
        void UpdateTrayActions() => _tray?.UpdateHotkeyPause(_window.DictationHotkeysPaused,
            _window.CanChangeDictationHotkeyPause, _window.DictationHotkeyPauseError);
        _window.TrayActionsChanged += UpdateTrayActions;
        void UpdateTrayRecorder() => _tray?.UpdateRecorder(_window.RecorderRecording, _window.CanToggleRecorder);
        _window.RecorderChanged += UpdateTrayRecorder;
        UpdateTrayRecorder();
        UpdateTrayActions();
        _window.DictationChanged += (status, recording) =>
        {
            _tray?.UpdateDictation(status, recording);
            _tray?.UpdateProcessing(_window.CanCancelProcessing);
            UpdateTrayActions();
        };
        var initialization = _window.InitializeDictationAsync();
        // Do not hold onboarding behind plugin discovery, downloads or account requests.
        if (presentation == TypeWhisper.Presentation.StartupPresentation.Setup) _window.OpenSetup(returnToTray: true);
        await initialization;
        UpdateTrayActions();
        _window.ShowMigrationNotice();
#if DEBUG
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_FIXTURE") == "1")
            _window.DispatcherQueue.TryEnqueue(_window.ShowHistoryFromTray);
        // Opt-in visual fixture: no capture, provider request, clipboard write or history entry.
        if (WinUIProfile.IsTestProfile && Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_REVIEW_FIXTURE") == "1")
            _window.DispatcherQueue.TryEnqueue(() => _window.ShowOutputReview(new(
                new TypeWhisper.Core.Models.TranscriptionRecord
                {
                    Id = "review-fixture", Timestamp = DateTime.UtcNow,
                    RawText = "Let's finish the release notes tomorrow.",
                    FinalText = "Let's finish the release notes tomorrow.\n\nWe can check the upgrade together before publishing."
                }, false, true, "TypeWhisper could not complete automatic insertion. Copy the text, check the intended field, and paste any missing text there.")
                { ReviewReason = TypeWhisper.Presentation.DictationReviewReason.PasteFailed, Failed = true }));
#endif
        _activationReady = true;
        _shareStartupReady.TrySetResult(true);
        await initialShare;
        DrainActivations();
    }

    private bool CanReceiveSharedActivation() => _activationReady && !_exiting && _profileOperation is null;

    private void DrainActivations()
    {
        if (_profileOperation is { } operation)
        {
            // While startup can still succeed (import in progress or retryable), queued activations wait for it.
            if (_shareStartupReady.Task.IsCompleted) _activations.Close();
            operation.Activate(); return;
        }
        if (_exiting) { _activations.Close(); return; }
        if (!_activationReady || _window is null) return;
        _activations.Dispatch(_window.HandleActivation, _window.ShowActivationFailure);
    }

    private bool _exiting;
    private void ExitAfterProfileOperation()
    {
        _mainInstance?.UnregisterKey();
        AppDiagnostics.Flush(TimeSpan.FromSeconds(2));
        Exit();
    }

    private void ShowProfileFailure(string message, string? details = null, bool keepStartupPending = false)
    {
        if (!keepStartupPending) _shareStartupReady.TrySetResult(false);
        if (_profileOperation is null) _profileOperation = new(message, false, CloseProfileOperation);
        else _profileOperation.SetMessage(message, false);
        _profileOperation.SetDetails(details);
        _profileOperation.Activate();
    }

    private bool _restartAfterProfileRestore;
    private async Task RestoreProfileAsync(TypeWhisper.Core.Services.PersistedProfileBackup store,
        TypeWhisper.Core.Services.PersistedProfileBackupPreview preview, bool restart = false)
    {
        if (_exiting || _window is null) return;
        _exiting = true;
        _restartAfterProfileRestore = restart;
        _shareStartupReady.TrySetResult(false);
        _profileOperation = new(Loc.T("Finishing active work before restoring your reviewed backup…"), true, CloseProfileOperation);
        _profileOperation.Activate();
        try
        {
            _tray?.Dispose(); _tray = null;
            _window.FreezeForProfileRestore();
            await CompleteProfileRestoreAsync(store, preview);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure(Loc.T("Could not stop active work for restoration. No backup was applied."), ex.Message);
        }
    }

    private async Task CompleteProfileRestoreAsync(TypeWhisper.Core.Services.PersistedProfileBackup store,
        TypeWhisper.Core.Services.PersistedProfileBackupPreview preview)
    {
        _profileOperation!.SetMessage(Loc.T("Finishing active work before restoring your reviewed backup…"), true);
        try
        {
            await _window!.ShutdownDictationAsync();
            // No dispatch or asynchronous continuation between publication and application exit.
            // All live runtime writers must have drained before Apply.
            var result = store.Apply(preview);
            if (result.Error is not null)
            {
                ShowProfileFailure(result.RecoveryRequired
                    ? Loc.T("The restore needs recovery before your profile can open again. Close TypeWhisper and reopen it to finish recovery.")
                    : Loc.T("The backup was not applied. Close and reopen TypeWhisper, then review the backup again."), result.Error);
                return;
            }
            if (_restartAfterProfileRestore)
            {
                _mainInstance?.UnregisterKey();
                var reason = AppInstance.Restart("");
                ShowProfileFailure(Loc.T("Settings were restored. Reopen TypeWhisper to use them."), reason.ToString());
                return;
            }
            ExitAfterProfileOperation();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_window?.CanRetryRecorderShutdown == true)
            {
                ShowProfileFailure(Loc.T("The current recording could not be saved. Its audio is still held in memory. Retry saving before restoring. Closing the app discards that unsaved audio."));
                _profileOperation!.OfferSaveRetry(() => CompleteProfileRestoreAsync(store, preview));
            }
            else ShowProfileFailure(Loc.T("Restore did not complete. Close and reopen TypeWhisper before continuing."), ex.Message);
        }
    }

    // "Delete all data" asks for confirmation in Sync & backup, then stops every writer the way a restore does,
    // deletes what this process no longer holds and restarts. Loaded models and other files still open until the
    // process ends are removed by the next launch, before any store opens the folder again.
    private async Task DeleteAllDataAsync()
    {
        if (_exiting || _window is null) return;
        _exiting = true;
        _shareStartupReady.TrySetResult(false);
        _profileOperation = new(Loc.T("Finishing active work before deleting your data…"), true, CloseProfileOperation, DataDeletionHeading);
        _profileOperation.Activate();
        try
        {
            _tray?.Dispose(); _tray = null;
            _window.FreezeForProfileRestore();
            await _window.ShutdownDictationAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure(Loc.T("Active work could not be stopped, so no data was deleted. Close TypeWhisper, reopen it and try again."), ex.Message);
            return;
        }
        try
        {
            _profileOperation.SetMessage(Loc.T("Deleting your TypeWhisper data…"), true);
            // Recorded first, so the deletion resumes on the next launch if the app ends during the cleanup below.
            ProfileDataEraser.RequestErasure(WinUIProfile.Root);
            // Start with Windows lives in the registration, not the profile; a new installation starts with it off.
            var startupError = await TurnOffStartupAsync();
            var cliError = await Task.Run(RemoveOwnCli);
            var report = await Task.Run(UserDataDeletion.FinishPending);
            if (startupError is not null || cliError is not null)
            {
                var leftovers = cliError is null ? Loc.T("turn off Start with Windows under General")
                    : startupError is null ? Loc.T("remove the command line tool under Advanced")
                    : Loc.T("turn off Start with Windows under General and remove the command line tool under Advanced");
                ShowProfileFailure(report.Complete
                    ? Loc.T("Your data was deleted, but not everything outside it could be undone. Reopen TypeWhisper and {0}.", leftovers)
                    : Loc.T("Not all data could be deleted yet, and not everything outside it could be undone. Reopen TypeWhisper to finish deleting your data, then {0}.", leftovers),
                    string.Join(Environment.NewLine, new[] { startupError, cliError }.OfType<string>()));
                return;
            }
            _mainInstance?.UnregisterKey();
            // On success this API ends the process; returning means the restart failed.
            var reason = AppInstance.Restart("");
            ShowProfileFailure(Loc.T("Your data was deleted. Reopen TypeWhisper to finish removing files that were still in use."), reason.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure(ProfileDataEraser.IsErasurePending(WinUIProfile.Root)
                ? Loc.T("Not all data could be deleted yet. Reopen TypeWhisper to finish deleting it.")
                : Loc.T("No data was deleted. Close TypeWhisper, reopen it and try again."), ex.Message);
        }
    }

    private static string DataDeletionHeading => Loc.T("Delete all data");

    /// <returns>Null once startup is off or cannot be changed in this build; otherwise why it stayed on.</returns>
    private static async Task<string?> TurnOffStartupAsync()
    {
        try
        {
            var state = await WindowsStartupRegistration.Create().SetEnabledAsync(false);
            // A registration that cannot be read may still be on, so it is reported like one that stayed on.
            return state.IsEnabled || state.Unknown ? state.Error ?? Loc.T("Windows still lists TypeWhisper as a startup app.") : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ex.Message; }
    }

    /// <returns>Null once the command line tool this profile installed is removed, or when there is none; otherwise why it stayed.</returns>
    private static string? RemoveOwnCli()
    {
        try
        {
            var cli = new TypeWhisper.Presentation.CliInstallation(WinUIProfile.Root, WinUIProfile.CliInstallDirectory);
            switch (cli.IsBoundTo(WinUIProfile.Root))
            {
                // No tool, or another profile's, such as the release app's next to a development build: it stays.
                case false: return null;
                case null: return Loc.T("The command line tool's profile setting could not be read, so it was left installed.");
            }
            // Bound to this profile but without the install record Remove relies on to know which files it owns.
            if (!cli.GetState().CanRemove)
                return Loc.T("The command line tool still points to this profile, but its install record is missing or unreadable, so it was left installed.");
            cli.Remove();
            // A changed file stays; a binding left behind would still point a terminal at this profile.
            return cli.GetState() is { Installed: false, CanRemove: false } && cli.IsBoundTo(WinUIProfile.Root) is false
                ? null : Loc.T("Some files of the command line tool were changed and stayed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ex.Message; }
    }

    // Runs before anything reads the profile, so a confirmed deletion is never undone by a store writing it back.
    private async Task<bool> FinishPendingDataDeletionAsync(TypeWhisper.Presentation.ApplicationActivationRequest request,
        Task initialShare, bool skipLegacyImport)
    {
        if (!ProfileDataEraser.IsErasurePending(WinUIProfile.Root)) return true;
        _profileOperation ??= new(Loc.T("Finishing the deletion of your TypeWhisper data…"), true, Exit, DataDeletionHeading);
        _profileOperation.SetMessage(Loc.T("Finishing the deletion of your TypeWhisper data…"), true);
        _profileOperation.Activate();
        var report = await Task.Run(UserDataDeletion.FinishPending);
        if (report.Complete) return true;
        var message = report.Refused
            ? Loc.T("TypeWhisper could not finish deleting your data because a data folder is a link or cannot be read. Nothing behind the link was touched.")
            : report.Remaining == 1
                ? Loc.T("1 item of your TypeWhisper data could not be deleted, usually because another program is using them. Close other programs that may use these files, then retry.")
                : Loc.T("{0:N0} items of your TypeWhisper data could not be deleted, usually because another program is using them. Close other programs that may use these files, then retry.", report.Remaining);
        var folder = TypeWhisper.WinUI.Platform.AppDistribution.ResolveShellVisiblePath(WinUIProfile.Root);
        ShowProfileFailure(message, folder, keepStartupPending: true);
        void Offer() => _profileOperation.OfferActions(
            Loc.T("Retry"), () => ContinueLaunchAsync(() => OpenProfileAsync(request, initialShare, skipLegacyImport)),
            Loc.T("Open TypeWhisper anyway"), () =>
            {
                // Opening while the marker stays would erase the profile again on this or the next launch.
                if (ProfileDataEraser.CancelPendingErasure(WinUIProfile.Root))
                    return ContinueLaunchAsync(() => OpenProfileAsync(request, initialShare, skipLegacyImport));
                ShowProfileFailure(Loc.T("The deletion could not be canceled because another program is using your TypeWhisper data folder. Close it, then try again."), folder, keepStartupPending: true);
                Offer();
                return Task.CompletedTask;
            });
        Offer();
        return false;
    }

    private async void CloseProfileOperation()
    {
        _profileOperation?.SetMessage(Loc.T("Finishing shutdown…"), true);
        try
        {
            // A failed recorder save may have left other already-started work draining.
            // Closing explicitly discards its unsaved audio, but never skips the other owners.
            if (_window is not null) await _window.DrainBeforeProfileExitAsync();
            ExitAfterProfileOperation();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowProfileFailure(Loc.T("Shutdown could not finish. Profile access remains stopped."), ex.Message);
        }
    }

    private async void ExitFromTray() => await ExitOrRestartAsync(restart: false);

    private async Task<string?> ExitOrRestartAsync(bool restart, Action? applyUpdate = null)
    {
        if (_exiting) return Loc.T("The app is already shutting down.");
        _exiting = true;
        _tray?.SetShutdownState(Loc.T("Finishing shutdown…"));
        _shareStartupReady.TrySetResult(false);
        try
        {
            if (_window is not null) await _window.ShutdownDictationAsync();
            _tray?.Dispose();
            _tray = null;
            if (restart)
            {
                // Both restart paths end this process; queued diagnostics would be lost.
                AppDiagnostics.Flush(TimeSpan.FromSeconds(2));
                if (applyUpdate is not null)
                {
                    applyUpdate();
                    ShowProfileFailure(Loc.T("The update restart did not complete. Close and reopen TypeWhisper."), null);
                    return Loc.T("The update restart did not complete.");
                }
                // Restart the same host only after all profile writers and native owners have drained.
                // On success this API terminates the process; returning means restart failed.
                var reason = AppInstance.Restart("");
                var message = Loc.T("Automatic restart failed. Close and reopen TypeWhisper to apply the plugin update.");
                ShowProfileFailure(message, reason.ToString());
                return message;
            }
            _mainInstance?.UnregisterKey();
            AppDiagnostics.Flush(TimeSpan.FromSeconds(2));
            Exit();
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.WriteFailure("app.shutdown.failed", ex);
            _tray?.SetShutdownState(Loc.T("Shutdown failed. Work is stopped."));
            _window?.ShowShutdownFailure();
            if (_window?.CanRetryRecorderShutdown == true)
            {
                _exiting = false;
                _tray?.AllowShutdownRetry();
            }
            return Loc.T("Shutdown could not finish. Resolve the displayed error before restarting.");
        }
    }
}
