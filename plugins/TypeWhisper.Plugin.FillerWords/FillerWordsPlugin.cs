using System.Reflection;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Removes filler words such as "um" and "uh" from transcribed text.
/// </summary>
public sealed class FillerWordsPlugin : IPostProcessorPlugin
{
    private static readonly string BuildVersion =
        typeof(FillerWordsPlugin).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? throw new InvalidOperationException("Plugin assembly does not define an informational version.");

    private IPluginHostServices? _host;

    /// <summary>Gets the stable plugin identifier used by the host.</summary>
    public string PluginId => "com.typewhisper.filler-words";

    /// <summary>Gets the plugin display name shown by the host.</summary>
    public string PluginName => "Filler Words";

    /// <summary>Gets the plugin version reported to the host.</summary>
    public string PluginVersion => BuildVersion;

    /// <summary>Gets the processor name shown in the post-processing pipeline.</summary>
    public string ProcessorName => "Filler Words";

    /// <summary>Gets the post-processing priority. Lower values run first.</summary>
    public int Priority => 250;

    /// <summary>Gets the settings store, or null when the plugin is not activated.</summary>
    public FillerWordsSettingsStore? Settings { get; private set; }

    internal IPluginLocalization? Loc => _host?.Localization;

    /// <summary>Activates the plugin and loads the configured filler word list.</summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        Settings = new FillerWordsSettingsStore(host);
        return Task.CompletedTask;
    }

    /// <summary>Deactivates the plugin.</summary>
    public Task DeactivateAsync()
    {
        Settings = null;
        _host = null;
        return Task.CompletedTask;
    }

    /// <summary>Creates the settings view shown by the host.</summary>
    public UserControl? CreateSettingsView() =>
        Settings is null ? null : new FillerWordsSettingsView(this, Settings);

    /// <summary>Removes the configured filler words from the transcription.</summary>
    public Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct) =>
        Task.FromResult(FillerWordFilter.Remove(text, Settings?.Words ?? FillerWordFilter.DefaultFillerWords));

    /// <summary>Releases plugin resources.</summary>
    public void Dispose()
    {
        Settings = null;
        _host = null;
    }
}
