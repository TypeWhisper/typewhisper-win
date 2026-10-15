using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private IStartupRegistration? _startupRegistration;
    private StartupPolicyReconciler? _startupPolicy;
    private IStartupRegistration StartupRegistration => _startupRegistration ??= WindowsStartupRegistration.Create();
    private StartupPolicyReconciler StartupPolicy => _startupPolicy ??=
        new(StartupRegistration, new StartupPolicyStore(WinUIProfile.DataPath("startup-policy.json")));
    private static bool SetupCompleted => new SetupPreferencesStore(WinUIProfile.DataPath("setup.json")).Current.Completed;

    // Runs on the UI thread after every policy change: the license key, cached update offers and the startup registration.
    internal void ApplyManagedPolicyChange()
    {
        if (_closing || _profileRestoreClosing) return;
        WinUILicensing.ApplyPolicyChange();
        _applicationUpdates?.PolicyChanged();
        _ = ApplyStartupPolicyAsync();
    }

    internal async Task ApplyStartupPolicyAsync()
    {
        try { await StartupPolicy.ApplyAsync(WinUIManagedPolicy.Current, SetupCompleted); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.startup.apply-failed", ex); }
    }

    // Policy may keep the setup wizard closed only when the app can actually dictate: a shortcut, a ready model and a
    // microphone. A model that is still loading gets a bounded wait; nothing is marked as completed.
    internal async Task<bool> IsSetupReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!_closing && !_dictation.CanChangeProvider && DateTimeOffset.UtcNow < deadline) await Task.Delay(500);
        if (_closing) return true;
        bool microphone;
        try { microphone = _dictation.GetMicrophones().Count > 0; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { microphone = false; }
        var hasShortcut = !string.IsNullOrWhiteSpace(_dictation.Shortcut) && _dictation.Shortcut != "No shortcut assigned";
        return OnboardingPolicy.CanSkipSetup(WinUIManagedPolicy.Current, hasShortcut, _dictation.CanChangeProvider && _dictation.IsReady, microphone);
    }
}
