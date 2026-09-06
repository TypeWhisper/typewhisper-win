using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSDK.PortableFixture;

public sealed class LifecycleProbePlugin : ITypeWhisperPlugin, IPluginInstallationLifecycle
{
    public string PluginId => "com.test.lifecycle";
    public string PluginName => "Lifecycle fixture";
    public string PluginVersion => "1.0.0";
    private IPluginHostServices? _host;
    public Task ActivateAsync(IPluginHostServices host) { _host = host; host.SetSetting("active", true); return Task.CompletedTask; }
    public Task DeactivateAsync() { _host?.SetSetting("active", false); _host?.SetSetting("unloaded", true); return Task.CompletedTask; }
    public Task OnInstallAsync(PluginInstallationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Host.GetSetting<bool>("fail-install")) throw new InvalidOperationException("Installation fixture failed.");
        context.Progress?.Report(new("Preparing fixture resources", 0.5));
        context.Host.SetSetting("installed", true);
        return Task.CompletedTask;
    }
    public Task OnUninstallAsync(PluginInstallationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Host.GetSetting<bool>("active")) throw new InvalidOperationException("Still active.");
        if (context.Host.GetSetting<bool>("fail-uninstall")) throw new InvalidOperationException("Uninstallation fixture failed.");
        context.Progress?.Report(new("Releasing fixture registrations"));
        context.Host.SetSetting("removed", true);
        return Task.CompletedTask;
    }
    public void Dispose() { }
}
