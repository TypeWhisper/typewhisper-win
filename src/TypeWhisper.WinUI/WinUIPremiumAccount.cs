using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class WinUIPremiumAccount
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private static PremiumAccountClient? _client;
    private static TaskCompletionSource<Uri>? _callback;
    private static CancellationTokenSource? _cancel;
    private static Task _operation = Task.CompletedTask;
    private static bool _closing;
    internal static bool Busy { get; private set; }
    internal static bool SignedIn => _client?.SignedIn == true;
    internal static bool Premium => _client?.Entitlement?.IsActive == true && SignedIn;
    internal static bool Waiting => _callback is not null;
    internal static string Status { get; private set; } = Loc.T("Sign in to manage your TypeWhisper Premium account.");
    internal static event Action? Changed;
    private static string SessionPath => WinUIProfile.DataPath("premium-account.dat");

    private static PremiumAccountClient Client
    {
        get
        {
            if (_client is not null) return _client;
            Directory.CreateDirectory(WinUIProfile.Root);
            var devicePath = WinUIProfile.DataPath("premium-account-device.txt");
            var id = File.Exists(devicePath) ? File.ReadAllText(devicePath).Trim() : Guid.NewGuid().ToString();
            if (!Guid.TryParse(id, out _)) throw new InvalidDataException(Loc.T("Invalid account device identifier."));
            if (!File.Exists(devicePath)) File.WriteAllText(devicePath, id);
            var saved = File.Exists(SessionPath) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(SessionPath), null, DataProtectionScope.CurrentUser)) : null;
            return _client = new(Http, id, Save, saved);
        }
    }
    private static void Save(string? value)
    {
        if (value is null) { File.Delete(SessionPath); return; }
        AtomicFileWriter.WriteAllBytes(SessionPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
    internal static Task RefreshAsync() => Run(async ct =>
    {
        await Client.RefreshAsync(ct);
        Status = SignedIn ? Premium ? Loc.T("Signed in · Premium active") : Loc.T("Signed in · No Premium entitlement") : Loc.T("Not signed in.");
    });
    internal static Task SignInAsync() => Run(async ct =>
    {
#if !TYPEWHISPER_STORE
        ActivationRegistrationManager.RegisterForProtocolActivation("typewhisper", "", "TypeWhisper", Environment.ProcessPath!);
#endif
        var address = await Client.BeginAsync(ct);
        _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Status = Loc.T("Finish signing in in your browser. This request expires in 10 minutes."); Changed?.Invoke();
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(address)) throw new InvalidOperationException(Loc.T("The browser could not be opened."));
            var callback = await _callback.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
            WinUILicensing.Service.TryGetCommercialAccountProof(out var key, out var activation);
            await Client.CompleteAsync(callback, key, activation, ct);
            Status = Premium ? Loc.T("Signed in · Premium active") : Loc.T("Signed in · No Premium entitlement");
        }
        finally { _callback = null; Client.Cancel(); }
    });
    internal static void ReceiveCallback(Uri uri)
    {
        try
        {
            if (_callback is not null && Client.Accepts(uri)) _callback.TrySetResult(uri);
            else { Status = Loc.T("No matching sign-in request. Start signing in again from Premium."); Changed?.Invoke(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.T("The Apple sign-in response could not be verified."); Changed?.Invoke(); }
    }
    internal static Task LinkAsync() => Run(async ct =>
    {
        if (!WinUILicensing.Service.TryGetCommercialAccountProof(out var key, out var activation)) throw new InvalidOperationException(Loc.T("Activate a commercial license first."));
        await Client.LinkAsync(key!, activation!, ct); Status = Premium ? Loc.T("Commercial license linked · Premium active") : Loc.T("The license did not grant Premium access.");
    });
    internal static Task SignOutAsync() => Run(async ct => { await Client.SignOutAsync(ct); Status = Loc.T("Signed out. Your local data and license are kept."); });
    internal static void Cancel() => _cancel?.Cancel();
    private static Task Run(Func<CancellationToken, Task> action)
    {
        if (Busy || _closing) return _operation;
        Busy = true; _cancel = new();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task; Changed?.Invoke();
        _ = Complete(action, completion); return _operation;
    }
    private static async Task Complete(Func<CancellationToken, Task> action, TaskCompletionSource completion)
    {
        try { await action(_cancel!.Token); }
        catch (OperationCanceledException) { Status = Loc.T("Sign-in canceled."); }
        catch (TimeoutException) { Status = Loc.T("Sign-in expired. Please try again."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Status = ex is InvalidDataException or InvalidOperationException or HttpRequestException ? ex.Message : Loc.T("Account access could not be completed. Please try again."); }
        finally
        {
            _callback = null; _client?.Cancel(); _cancel?.Dispose(); _cancel = null; Busy = false;
            Changed?.Invoke(); PremiumView.Access.NotifyActualAccessChanged(); completion.TrySetResult();
        }
    }
    internal static Task ShutdownAsync() { _closing = true; Cancel(); return _operation; }
}
