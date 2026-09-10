namespace TypeWhisper.PluginSDK;

/// <summary>Optional package lifecycle, independent of runtime activation.</summary>
public interface IPluginInstallationLifecycle
{
    /// <summary>
    /// Prepares a verified installation before it becomes available. PreviousVersion is null
    /// on first install. Must be idempotent: failed or interrupted installs can be retried.
    /// Do not start runtime services or require model downloads here; use ActivateAsync for loading.
    /// </summary>
    Task OnInstallAsync(PluginInstallationContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs after runtime deactivation, before unregistering the package. Keep user settings,
    /// secrets and downloaded models. Failure leaves the plugin installed and disabled.
    /// </summary>
    Task OnUninstallAsync(PluginInstallationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Services and version context for a package lifecycle operation.</summary>
/// <param name="Host">Services scoped to this plugin's persistent data.</param>
/// <param name="PreviousVersion">Installed version being replaced or removed; null for a new installation.</param>
/// <param name="Progress">Optional host-rendered status and progress, never a plugin-owned window.</param>
public sealed record PluginInstallationContext(IPluginHostServices Host, string? PreviousVersion,
    IProgress<PluginInstallationProgress>? Progress = null);

/// <summary>A plain-text lifecycle status. Never include API keys or other secrets.</summary>
/// <param name="Message">Current work, for example preparing a runtime or releasing resources.</param>
/// <param name="Fraction">Progress of this step from zero to one, or null when unknown.</param>
public sealed record PluginInstallationProgress(string Message, double? Fraction = null);
