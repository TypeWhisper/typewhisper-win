using System.Reflection;
using TypeWhisper.Plugin.Shared;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Removes filler words such as "um" and "uh" from transcribed text and collapses
/// stuttered repetitions such as "I I I".
/// </summary>
public sealed class FillerWordsPlugin : IPostProcessorPlugin, IPluginTextSettings
{
    private const string WordsSettingId = "words";
    private const string CollapseRepeatedWordsSettingId = "collapseRepeatedWords";

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

    private string L(string en, string de) => PluginLocalization.Get(_host, en, de);

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


    /// <summary>
    /// Removes the configured filler words from the transcription and, unless turned off,
    /// collapses stuttered repetitions. Words that are real words in other languages are
    /// only removed when the dictation language matches.
    /// </summary>
    public Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = Settings;
        var result = FillerWordFilter.Remove(text, settings?.Words ?? FillerWordFilter.DefaultFillerWords, context.SourceLanguage);
        if (settings?.CollapseRepeatedWords ?? true)
            result = RepeatedWordCollapser.Collapse(result);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => Settings is not { } settings ? [] :
    [
        new(WordsSettingId, L("Filler words", "Füllwörter"),
            L("Enter one word or phrase per line. Words that are real words in other languages, such as \"um\", are only removed when the dictation language matches.",
              "Ein Wort oder eine Wendung pro Zeile. Wörter, die in anderen Sprachen echte Wörter sind, etwa „um“, werden nur entfernt, wenn die Diktatsprache passt."),
            settings.WordsText) { IsMultiline = true },
        new(CollapseRepeatedWordsSettingId, L("Collapse repeated words", "Wiederholte Wörter zusammenfassen"),
            L("Shortens a word that occurs three or more times in a row, such as \"I I I\", to a single word.",
              "Kürzt ein Wort, das drei- oder mehrmals hintereinander vorkommt, etwa „ich ich ich“, auf ein Wort."),
            settings.CollapseRepeatedWords ? "true" : "false")
        { Choices = [new("true", L("On", "Ein")), new("false", L("Off", "Aus"))] }
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id is not (WordsSettingId or CollapseRepeatedWordsSettingId)) throw new ArgumentException("Unknown setting.", nameof(id));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 32768) throw new ArgumentException("The filler word list is too long.", nameof(value));
        if (Settings is null) throw new InvalidOperationException("Enable the plugin before configuring it.");
        if (id == WordsSettingId)
        {
            Settings.WordsText = value;
            return Task.CompletedTask;
        }

        Settings.CollapseRepeatedWords = value switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException("Unsupported setting value.", nameof(value))
        };
        return Task.CompletedTask;
    }

    /// <summary>Releases plugin resources.</summary>
    public void Dispose()
    {
        Settings = null;
        _host = null;
    }
}
