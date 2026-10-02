using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Page layout only. LiveDictationSettings and the other live settings bind each row to saved preferences.
internal static partial class SettingsCatalog
{
    private static void RenderDictation(StackPanel target, Dictionary<string, string> values, List<ChoicePicker> pickers)
    {
        SettingsCard Card(string heading, params string[] keys)
        {
            var card = new SettingsCard(heading);
            RenderFields(keys.Select(key => Fields.Single(field => field.Key == key)), card, values, pickers, null);
            target.Children.Add(card);
            return card;
        }

        var model = Card(Loc.T("Model"), "Language", "LanguageHints", "TranscriptionTask", "TranslationTargetLanguage");
        // LiveDictationSettings fills this row with the provider and adds the model row below it.
        model.Children.Insert(1, new SettingsRow("DictationModel"));
        Card(Loc.T("Recording"), "Mode", "AutoPaste", "LockPasteToFocusedField");
        var text = Card(Loc.T("Text"), "TranscriptionNumberNormalizationEnabled", "ShortUtterancePunctuationEnabled",
            "EnglishOutputVariant", "GermanOutputVariant");

        var vocabularyToggle = AppToggleSwitch.Create(DictionaryBoostingPreferences.Load());
        AutomationProperties.SetName(vocabularyToggle, Loc.T("Vocabulary boosting"));
        var vocabulary = new SettingsRow("VocabularyBoostingEnabled").Set(Loc.T("Vocabulary boosting"), "",
            Loc.T("Uses text-based matching. Manage words and Term packs in Settings > Dictionary. Acoustic vocabulary support depends on the selected plugin."),
            vocabularyToggle);
        var restoringVocabulary = false;
        vocabularyToggle.Toggled += (_, _) =>
        {
            if (restoringVocabulary) return;
            var error = DictionaryBoostingPreferences.Save(vocabularyToggle.IsOn);
            vocabulary.Status = error ?? "";
            if (error is not null) { restoringVocabulary = true; vocabularyToggle.IsOn = !vocabularyToggle.IsOn; restoringVocabulary = false; }
        };
        text.Children.Add(vocabulary);
        RenderFields(Fields.Where(field => field.Key == "SpokenFormattingProfiles"), text, values, pickers, null);
    }
}
