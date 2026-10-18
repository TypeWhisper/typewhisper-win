using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private IStartupRegistration? _startupRegistration;
    private StartupPolicyReconciler? _startupPolicy;
    private IStartupRegistration StartupRegistration => _startupRegistration ??= WindowsStartupRegistration.Create();
    private StartupPolicyReconciler StartupPolicy => _startupPolicy ??=
        new(StartupRegistration, new StartupPolicyStore(WinUIProfile.DataPath("startup-policy.json")));
    // An unreadable setup state is not a new profile: recommendations meant for new profiles stay off until it is readable.
    private static bool SetupCompleted
    {
        get
        {
            var store = new SetupPreferencesStore(WinUIProfile.DataPath("setup.json"));
            return store.Error is not null || store.Current.Completed;
        }
    }
    private readonly SemaphoreSlim _startupPolicyGate = new(1, 1);

    /// <summary>Set by the app when policy kept an unfinished setup closed; a policy that no longer allows it reopens setup.</summary>
    internal bool SetupSkippedByPolicy { get; set; }

    // Runs on the UI thread after every policy change: the license key, cached update offers, the startup registration
    // and a setup that policy no longer keeps closed.
    internal void ApplyManagedPolicyChange()
    {
        if (_closing || _profileRestoreClosing) return;
        WinUILicensing.ApplyPolicyChange();
        _applicationUpdates?.PolicyChanged();
        _ = ApplyStartupPolicyAsync();
        _ = _dictation.ApplyPolicyChangeAsync();
        if (SetupSkippedByPolicy && !OnboardingPolicy.SkipSetupWhenReady(WinUIManagedPolicy.Current) && !SetupCompleted)
        {
            SetupSkippedByPolicy = false;
            OpenSetup(returnToTray: true);
        }
    }

    // Reconciliations run one after another and read the policy once they hold the gate, so the last run applies
    // the newest snapshot and an older read or write can never overwrite a newer value.
    internal async Task<StartupPolicyOutcome?> ApplyStartupPolicyAsync()
    {
        await _startupPolicyGate.WaitAsync();
        try { return await StartupPolicy.ApplyAsync(WinUIManagedPolicy.Current, SetupCompleted); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.startup.apply-failed", ex); return null; }
        finally { _startupPolicyGate.Release(); }
    }

    // A user's own change takes the same gate and re-reads the policy first: an enforced value that arrived meanwhile
    // wins and the row shows the locked state instead of a write that policy would undo.
    internal async Task<StartupPolicyOutcome?> ChangeStartupFromUserAsync(bool enabled)
    {
        await _startupPolicyGate.WaitAsync();
        try
        {
            var snapshot = WinUIManagedPolicy.Current;
            if (snapshot.IsBlocked(ManagedPolicyKeys.LaunchAtLogin) || snapshot.IsEnforced(ManagedPolicyKeys.LaunchAtLogin))
                return await StartupPolicy.ApplyAsync(snapshot, SetupCompleted);
            var state = await StartupRegistration.SetEnabledAsync(enabled);
            // A choice that could not be recorded is shown: a recommendation could otherwise replace it after a restart.
            var recorded = state.Error is not null || StartupPolicy.RecordUserChoice(enabled);
            return new(state, false, null, recorded ? null : StartupPolicy.Store.Error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.startup.change-failed", ex); return null; }
        finally { _startupPolicyGate.Release(); }
    }

    // Policy may keep the setup wizard closed only when the app can actually dictate: a shortcut, a ready model and a
    // microphone. A model that is still loading gets a bounded wait; nothing is marked as completed.
    internal async Task<bool> IsSetupReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!_closing && !_dictation.CanChangeProvider && DateTimeOffset.UtcNow < deadline) await Task.Delay(500);
        if (_closing) return true;
        bool microphone;
        // Stricter than the wizard's step check: a device must exist and Windows must have confirmed access.
        try { microphone = _dictation.GetMicrophones().Count > 0 && SetupWizard.MicrophoneAccessAllowed; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { microphone = false; }
        var hasShortcut = !string.IsNullOrWhiteSpace(_dictation.Shortcut) && _dictation.Shortcut != "No shortcut assigned";
        return OnboardingPolicy.CanSkipSetup(WinUIManagedPolicy.Current, hasShortcut, _dictation.CanChangeProvider && _dictation.IsReady, microphone);
    }
}
