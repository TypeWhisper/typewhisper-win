using TypeWhisper.Presentation;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.WinUI;

// One service and one operation for the whole profile, independent of settings view lifetime.
internal static class WinUILicensing
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static LicenseService Service { get; } = new(Http, Path.GetDirectoryName(WinUIProfile.DataPath("licenses.dat"))!);
    internal static PremiumAccess Current => new(Commercial: Service.HasCommercialLicense, Supporter: Service.HasSupporterLicense);
    internal static event Action? Changed;
    internal static string? Notice { get; private set; }
    internal static bool Busy { get; private set; }
    private static bool _closing;
    private static Task _operation = Task.CompletedTask;
    static WinUILicensing() => Service.StatusChanged += () => Changed?.Invoke();

    internal static Task ValidateAsync() => RunAsync(async () => { await Service.ValidateAllIfNeededAsync(); });
    internal static Task ActivateAsync(string key) => RunAsync(async () =>
    {
        var entitlement = await Service.ActivateAnyLicenseKeyAsync(key);
        Notice = Service.LicenseActivationError ?? (entitlement is null ? "Enter your license key." : "License activated on this device.");
    });
    internal static Task RefreshAsync(bool commercial) => RunAsync(async () =>
    {
        if (commercial) await Service.RefreshCommercialLicenseAsync();
        else await Service.RefreshSupporterLicenseAsync();
        Notice = (commercial ? Service.CommercialRefreshError : Service.SupporterRefreshError) ?? "License status updated.";
    });
    internal static Task DeactivateAsync(bool commercial) => RunAsync(async () =>
    {
        if (commercial) await Service.DeactivateCommercialLicenseAsync();
        else await Service.DeactivateSupporterLicenseAsync();
        Notice = (commercial ? Service.CommercialDeactivationError : Service.SupporterDeactivationError) ?? "This device was deactivated.";
    });
    private static Task RunAsync(Func<Task> action)
    {
        if (_closing || Busy) return _operation;
        Busy = true; Notice = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        Changed?.Invoke();
        _ = CompleteAsync(action, completion);
        return completion.Task;
    }
    private static async Task CompleteAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Notice = "The license operation could not be completed. Please try again."; }
        finally { Busy = false; Changed?.Invoke(); completion.TrySetResult(); }
    }
    internal static Task ShutdownAsync() { _closing = true; return _operation; }
}
