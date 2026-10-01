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
        output.Children.Add(Label(Loc.T("After recording"), 14));
        var outputPicker = new ChoicePicker();
        outputPicker.Configure(Loc.T("After recording"), "text", "Preference AutoPaste");
        outputPicker.SetOptions([
            new("On", Loc.T("Insert directly"), Loc.T("Send the finished text to the active text field.")),
            new("Off", Loc.T("Review first"), Loc.T("Show the result before you choose where to use it."))
        ], values.GetValueOrDefault("AutoPaste", "On"));
        outputPicker.SelectionChanged += selected => { values["AutoPaste"] = selected; Update(); };
        pickers.Add(outputPicker); output.Children.Add(outputPicker);
        var outputHint = Label("", 12, true); output.Children.Add(outputHint);
        updates.Add(() => outputHint.Text = values.GetValueOrDefault("AutoPaste", "On") == "On"
            ? Loc.T("The finished transcript goes straight into your active text field.")
            : Loc.T("Review the transcript, then copy or insert it. No automatic paste."));
        target.Children.Add(output);

        // One disclosure instead of a separate box for every setting. No layout animation.
        var advanced = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed };
        var expandButton = new HandCursorButton
        {
            Content = Loc.T("More options  +"), MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"]
        };
        void ShowAdvanced(bool show)
        {
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            expandButton.Content = show ? Loc.T("More options  −") : Loc.T("More options  +");
            AutomationProperties.SetItemStatus(expandButton, show ? Loc.T("Expanded") : Loc.T("Collapsed"));
        }
        // Search can reveal the disclosure without changing the preferences inside it.
        advanced.Tag = (Action)(() => ShowAdvanced(true));
        expandButton.Click += (_, _) => ShowAdvanced(advanced.Visibility != Visibility.Visible);
        AutomationProperties.SetName(expandButton, Loc.T("More dictation options"));
        AutomationProperties.SetHelpText(expandButton, Loc.T("Show or hide recording, translation and formatting options."));
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
        AutomationProperties.SetName(vocabularyToggle, Loc.T("Vocabulary boosting"));
        var vocabularyRow = new Grid(); vocabularyRow.ColumnDefinitions.Add(new()); vocabularyRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        vocabularyRow.Children.Add(SettingsHelp.Label(Loc.T("Vocabulary boosting"), Loc.T("Uses text-based matching. Manage words and Term packs in Settings > Dictionary. Acoustic vocabulary support depends on the selected plugin.")));
        Grid.SetColumn(vocabularyToggle, 1); vocabularyRow.Children.Add(vocabularyToggle); vocabulary.Children.Add(vocabularyRow);
        var vocabularyHint = Label(Loc.T("Saved for dictation."), 12, true);
        vocabulary.Children.Add(vocabularyHint);
        var restoringVocabulary = false;
        vocabularyToggle.Toggled += (_, _) =>
        {
            if (restoringVocabulary) return;
            var error = DictionaryBoostingPreferences.Save(vocabularyToggle.IsOn);
            vocabularyHint.Text = error ?? Loc.T("Saved for the next dictation.");
            if (error is not null) { restoringVocabulary = true; vocabularyToggle.IsOn = !vocabularyToggle.IsOn; restoringVocabulary = false; }
        };
        advanced.Children.Add(vocabulary);
        FieldsInto(advanced, "SpokenFormattingProfiles");
        target.Children.Add(Label(Loc.T("Changes on this page are saved automatically."), 12, true));
        Update();
    }
}
