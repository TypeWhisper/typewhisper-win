using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Page layout only. LiveDictationSettings and the other live settings bind each row to saved preferences.
internal static partial class SettingsCatalog
{
    private static void RenderDictation(StackPanel target, Dictionary<string, string> values, List<ChoicePicker> pickers)
    {
        var updates = new List<Action>();
        void Update() { foreach (var update in updates) update(); }
        void FieldsInto(StackPanel panel, params string[] keys) => RenderFields(
            keys.Select(key => Fields.Single(field => field.Key == key)), panel, values, pickers, Update);
        StackPanel Conditional(Func<bool> visible, params string[] keys)
        {
            var panel = new StackPanel { Spacing = 16 };
            FieldsInto(panel, keys);
            updates.Add(() => panel.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed);
            return panel;
        }

        // LiveDictationSettings fills this row with the provider and model pickers.
        target.Children.Add(new StackPanel { Spacing = 8, Tag = "DictationModel" });
        FieldsInto(target, "Language");

        var output = new StackPanel { Spacing = 8, Tag = "AutoPaste" };
        output.Children.Add(Label("After recording", 14));
        var outputPicker = new ChoicePicker();
        outputPicker.Configure("After recording", "text", "Preference AutoPaste");
        outputPicker.SetOptions([
            new("On", "Insert directly", "Send the finished text to the active text field."),
            new("Off", "Review first", "Show the result before you choose where to use it.")
        ], values.GetValueOrDefault("AutoPaste", "On"));
        outputPicker.SelectionChanged += selected => { values["AutoPaste"] = selected; Update(); };
        pickers.Add(outputPicker); output.Children.Add(outputPicker);
        var outputHint = Label("", 12, true); output.Children.Add(outputHint);
        updates.Add(() => outputHint.Text = values.GetValueOrDefault("AutoPaste", "On") == "On"
            ? "The finished transcript goes straight into your active text field."
            : "Review the transcript, then copy or insert it. No automatic paste.");
        target.Children.Add(output);

        // One disclosure instead of a separate box for every setting. No layout animation.
        var advanced = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed };
        var expandButton = new HandCursorButton
        {
            Content = "More options  +", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"]
        };
        void ShowAdvanced(bool show)
        {
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            expandButton.Content = show ? "More options  −" : "More options  +";
            AutomationProperties.SetItemStatus(expandButton, show ? "Expanded" : "Collapsed");
        }
        // Search can reveal the disclosure without changing the preferences inside it.
        advanced.Tag = (Action)(() => ShowAdvanced(true));
        expandButton.Click += (_, _) => ShowAdvanced(advanced.Visibility != Visibility.Visible);
        AutomationProperties.SetName(expandButton, "More dictation options");
        AutomationProperties.SetHelpText(expandButton, "Show or hide recording, translation and formatting options.");
        ShowAdvanced(false);
        target.Children.Add(expandButton); target.Children.Add(advanced);
        FieldsInto(advanced, "Mode");
        advanced.Children.Add(Conditional(() => values.GetValueOrDefault("Language", "Automatic") == "Automatic", "LanguageHints"));
        FieldsInto(advanced, "TranscriptionTask");
        advanced.Children.Add(Conditional(() => values.GetValueOrDefault("TranscriptionTask", "Transcribe") == "Translate", "TranslationTargetLanguage"));
        advanced.Children.Add(Conditional(() => values.GetValueOrDefault("AutoPaste", "On") == "On", "LockPasteToFocusedField"));
        FieldsInto(advanced, "TranscriptionNumberNormalizationEnabled", "ShortUtterancePunctuationEnabled",
            "EnglishOutputVariant", "GermanOutputVariant");
        var vocabulary = new StackPanel { Spacing = 8, Tag = "VocabularyBoostingEnabled" };
        var vocabularyToggle = AppToggleSwitch.Create(DictionaryBoostingPreferences.Load());
        AutomationProperties.SetName(vocabularyToggle, "Vocabulary boosting");
        var vocabularyRow = new Grid(); vocabularyRow.ColumnDefinitions.Add(new()); vocabularyRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        vocabularyRow.Children.Add(SettingsHelp.Label("Vocabulary boosting", "Uses text-based matching. Manage words and Term packs in Settings > Dictionary. Acoustic vocabulary support depends on the selected plugin."));
        Grid.SetColumn(vocabularyToggle, 1); vocabularyRow.Children.Add(vocabularyToggle); vocabulary.Children.Add(vocabularyRow);
        var vocabularyHint = Label("Saved for dictation.", 12, true);
        vocabulary.Children.Add(vocabularyHint);
        var restoringVocabulary = false;
        vocabularyToggle.Toggled += (_, _) =>
        {
            if (restoringVocabulary) return;
            var error = DictionaryBoostingPreferences.Save(vocabularyToggle.IsOn);
            vocabularyHint.Text = error ?? "Saved for the next dictation.";
            if (error is not null) { restoringVocabulary = true; vocabularyToggle.IsOn = !vocabularyToggle.IsOn; restoringVocabulary = false; }
        };
        advanced.Children.Add(vocabulary);
        FieldsInto(advanced, "SpokenFormattingProfiles");
        target.Children.Add(Label("Changes on this page are saved automatically.", 12, true));
        Update();
    }
}
