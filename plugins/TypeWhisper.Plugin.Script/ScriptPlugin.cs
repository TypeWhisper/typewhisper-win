using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Script;

/// <summary>Processes transcriptions through user-configured shell commands.</summary>
public sealed class ScriptPlugin : IPostProcessorPlugin
{
    /// <summary>Gets the stable plugin identifier.</summary>
    public string PluginId => "com.typewhisper.script";

    /// <summary>Gets the display name.</summary>
    public string PluginName => "Script Runner";

    /// <summary>Gets the plugin version.</summary>
    public string PluginVersion => "1.1.0";

    /// <summary>Gets the processor name.</summary>
    public string ProcessorName => "Script Runner";

    /// <summary>Gets the post-processing priority.</summary>
    public int Priority => 400;

    /// <summary>Gets the active script service.</summary>
    public ScriptService? Service { get; private set; }

    /// <summary>Activates the plugin and loads its configuration.</summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        Service = new ScriptService(host);
        return Task.CompletedTask;
    }

    /// <summary>Deactivates the plugin.</summary>
    public Task DeactivateAsync()
    {
        Service?.Dispose();
        Service = null;
        return Task.CompletedTask;
    }

    /// <summary>Runs the enabled script chain.</summary>
    public Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct) =>
        Service is null ? Task.FromResult(text) : Service.RunScriptsAsync(text, context, ct);

    /// <summary>Creates the settings view.</summary>
    public UserControl? CreateSettingsView() => Service is null ? null : new ScriptSettingsView(Service);

    /// <summary>Releases plugin resources.</summary>
    public void Dispose()
    {
        Service?.Dispose();
        Service = null;
    }
}
