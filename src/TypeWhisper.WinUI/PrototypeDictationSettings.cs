using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Isolated page composition. All choices stay in the existing preview session.
internal static partial class PrototypeSettingsCatalog
{
    private static void RenderDictation(StackPanel target, Dictionary<string, string> values, List<PrototypeChoicePicker> pickers)
    {
        target.Children.Add(Label("Your voice, your language, your words where you need them.", 13, true));
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

        var models = new PrototypeModelSession(values);
        var modelRow = new StackPanel { Spacing = 8, Tag = "DictationModel" };
        modelRow.Children.Add(Label("Model", 14));
        var modelPicker = new PrototypeChoicePicker();
        modelPicker.Configure("Model", "chip", "Dictation model");
        modelPicker.SetOptions(PrototypeModelSession.Models.Where(model => models.IsDownloaded(model.Id))
            .Select(model => new PrototypeChoice(model.Id, model.Title, model.Description)).ToArray(), models.Active,
            "Choose a downloaded model");
        var modelHint = Label("Shared with Models. Download more sample models there.", 12, true);
        modelPicker.SelectionChanged += id =>
        {
            if (models.Activate(id)) modelHint.Text = "Default model updated for this session, including Recorder unless overridden.";
        };
        modelRow.Children.Add(modelPicker); modelRow.Children.Add(modelHint);
        if (!PrototypeModelSession.Models.Any(model => models.IsDownloaded(model.Id)))
        {
            modelPicker.IsEnabled = false;
            modelHint.Text = "No models available. Download a sample in Models first.";
        }
        pickers.Add(modelPicker); target.Children.Add(modelRow);
        FieldsInto(target, "Language");

        var output = new StackPanel { Spacing = 8, Tag = "AutoPaste" };
        output.Children.Add(Label("After recording", 14));
        var outputPicker = new PrototypeChoicePicker();
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
            Content = "Advanced  +", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"]
        };
        void ShowAdvanced(bool show)
        {
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            expandButton.Content = show ? "Advanced  −" : "Advanced  +";
            AutomationProperties.SetItemStatus(expandButton, show ? "Expanded" : "Collapsed");
        }
        // Search can reveal the disclosure without changing the preferences inside it.
        advanced.Tag = (Action)(() => ShowAdvanced(true));
        expandButton.Click += (_, _) => ShowAdvanced(advanced.Visibility != Visibility.Visible);
        AutomationProperties.SetName(expandButton, "Advanced dictation settings");
        AutomationProperties.SetHelpText(expandButton, "Show or hide recording, translation and formatting options.");
        ShowAdvanced(false);
        target.Children.Add(expandButton); target.Children.Add(advanced);
        FieldsInto(advanced, "Mode");
        advanced.Children.Add(Conditional(() => values.GetValueOrDefault("Language", "Automatic") == "Automatic", "LanguageHints"));
        FieldsInto(advanced, "TranscriptionTask");
        advanced.Children.Add(Conditional(() => values.GetValueOrDefault("TranscriptionTask", "Transcribe") == "Translate", "TranslationTargetLanguage"));
        advanced.Children.Add(Conditional(() => values.GetValueOrDefault("AutoPaste", "On") == "On", "LockPasteToFocusedField"));
        FieldsInto(advanced, "TranscriptionNumberNormalizationEnabled", "ShortUtterancePunctuationEnabled",
            "EnglishOutputVariant", "GermanOutputVariant", "TranscribeShortQuietClipsAggressively", "VocabularyBoostingEnabled");
        advanced.Children.Add(Conditional(() => values.GetValueOrDefault("VocabularyBoostingEnabled", "Off") == "On",
            "VocabularyBoostingEnabledPackIds", "VocabularyBoostingSelectedIndustryPresetId"));
        FieldsInto(advanced, "SpokenFormattingProfiles");
        target.Children.Add(Label("Preview only · sample models, no recording or text insertion. Changes last for this session.", 12, true));
        Update();
    }
}
