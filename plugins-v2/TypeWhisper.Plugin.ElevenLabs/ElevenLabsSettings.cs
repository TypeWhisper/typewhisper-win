using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.ElevenLabs;

public sealed partial class ElevenLabsPlugin
{
    private bool German
    {
        get
        {
            try { return _host?.Localization.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true; }
            catch (NotSupportedException) { return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de"; }
        }
    }
    private string L(string en, string de) => German ? de : en;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(TranscriptionModeSettingName, L("Transcription mode", "Transkriptionsmodus"),
            L("Automatic shows live text. Audio events, multiple speakers or dictionary terms use the complete recording.",
              "Automatisch zeigt Live-Text. Audioereignisse, mehrere Sprecher oder WÃ¶rterbuchbegriffe verwenden die vollstÃ¤ndige Aufnahme."),
            FormatTranscriptionMode(_transcriptionMode))
        { Choices = [new("automatic", L("Automatic", "Automatisch")), new("restOnly", L("After recording", "Nach der Aufnahme"))] },
        Toggle(NoVerbatimSettingName, L("Clean transcript", "Bereinigtes Transkript"),
            L("Remove filler words and false starts.", "FÃ¼llwÃ¶rter und SatzabbrÃ¼che entfernen."), _noVerbatim),
        Toggle(TagAudioEventsSettingName, L("Audio events", "Audioereignisse"),
            L("Include events such as laughter in recorded-audio transcription.", "Ereignisse wie Lachen in die Transkription aufnehmen."), _tagAudioEvents),
        new(SpeakerCountSettingName, L("Speaker count", "Sprecherzahl"),
            L("Expected speakers in the recording.", "Erwartete Anzahl der Sprecher in der Aufnahme."), _speakerCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
        { Choices = Enumerable.Range(0, 33).Select(n => new PluginSettingChoice(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n == 0 ? L("Automatic", "Automatisch") : n.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray() },
        Toggle(UseDictionaryTermsSettingName, L("Dictionary terms", "WÃ¶rterbuchbegriffe"),
            L("Use active TypeWhisper dictionary terms to improve recognition.", "Aktive TypeWhisper-WÃ¶rterbuchbegriffe zur Erkennung verwenden."), _useDictionaryTerms)
    ];

    private PluginTextSetting Toggle(string id, string title, string description, bool value) =>
        new(id, title, description, FormatBoolean(value))
        { Choices = [new("true", L("On", "Ein")), new("false", L("Off", "Aus"))] };

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is null) throw new InvalidOperationException("Activate the plugin first.");
        var field = TextSettings.SingleOrDefault(field => field.Id == id);
        if (field is null || !field.Choices.Any(choice => choice.Value == value))
            throw new ArgumentException("Unsupported ElevenLabs setting value.");
        switch (id)
        {
            case TranscriptionModeSettingName: SetTranscriptionMode(NormalizeTranscriptionMode(value)); break;
            case NoVerbatimSettingName: SetNoVerbatim(bool.Parse(value)); break;
            case TagAudioEventsSettingName: SetTagAudioEvents(bool.Parse(value)); break;
            case SpeakerCountSettingName: SetSpeakerCount(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case UseDictionaryTermsSettingName: SetUseDictionaryTerms(bool.Parse(value)); break;
        }
        _host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }
}
