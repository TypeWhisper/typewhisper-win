using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveTextProcessingSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var store = session.TextPreferences;
        var refreshers = new List<Action>();
        void RefreshAll() { foreach (var refresh in refreshers) refresh(); }

        void AddToggle(string key, string title, string description, Func<DictationTextPreferences, bool> get,
            Func<DictationTextPreferences, bool, DictationTextPreferences> update)
        {
            var row = SettingsRow.Require(content, key).Reset(pickers);
            var toggle = AppToggleSwitch.Create(get(store.Current));
            AutomationProperties.SetName(toggle, title);
            AutomationProperties.SetHelpText(toggle, Loc.T("{0} Changes apply to the next recording.", description));
            row.Set(title, description, toggle);
            var restoring = false;
            refreshers.Add(() =>
            {
                restoring = true;
                toggle.IsOn = get(store.Current);
                restoring = false;
                row.Status = store.Error ?? "";
            });
            toggle.Toggled += (_, _) =>
            {
                if (restoring) return;
                store.Save(update(store.Current, toggle.IsOn));
                RefreshAll();
            };
        }

        void AddChoice(string key, string title, IReadOnlyList<Choice> options,
            Func<DictationTextPreferences, string> get, Func<DictationTextPreferences, string, DictationTextPreferences> update,
            string description)
        {
            var row = SettingsRow.Require(content, key).Reset(pickers);
            var picker = new ChoicePicker();
            picker.Configure(title, "language", "Preference " + key);
            row.Set(title, description, picker); pickers.Add(picker);
            refreshers.Add(() =>
            {
                picker.SetOptions(options, get(store.Current));
                row.Status = store.Error ?? "";
            });
            picker.SelectionChanged += id =>
            {
                if (options.Any(option => option.Id == id)) store.Save(update(store.Current, id));
                RefreshAll();
            };
        }

        AddToggle("TranscriptionNumberNormalizationEnabled", Loc.T("Normalize numbers"),
            Loc.T("Write spoken numbers as digits where appropriate for the transcript language."),
            preferences => preferences.TranscriptionNumberNormalizationEnabled,
            (preferences, enabled) => preferences with { TranscriptionNumberNormalizationEnabled = enabled });
        AddToggle("ShortUtterancePunctuationEnabled", Loc.T("Keep punctuation in short phrases"),
            Loc.T("Keep model punctuation in one- or two-word phrases. When off, remove greeting commas and ending punctuation from these phrases."),
            preferences => preferences.ShortUtterancePunctuationEnabled,
            (preferences, enabled) => preferences with { ShortUtterancePunctuationEnabled = enabled });
        AddChoice("EnglishOutputVariant", Loc.T("English spelling"), [
            new("AsTranscribed", Loc.T("As transcribed"), Loc.T("Keep spelling from transcription and corrections.")),
            new("UnitedStates", Loc.T("American"), Loc.T("Apply American spelling to recognized English output.")),
            new("UnitedKingdom", Loc.T("British"), Loc.T("Apply British spelling to recognized English output."))
        ], preferences => preferences.EnglishOutputVariant.ToString(),
            (preferences, id) => preferences with { EnglishOutputVariant = Enum.Parse<EnglishOutputVariant>(id) },
            Loc.T("Applied after snippets and dictionary corrections. Requires English to be detected or selected as the spoken language; automatic language without detection leaves spelling unchanged."));
        AddChoice("GermanOutputVariant", Loc.T("German spelling"), [
            new("AsTranscribed", Loc.T("As transcribed"), Loc.T("Keep spelling from transcription and corrections.")),
            new("Switzerland", Loc.T("Swiss Standard German"), Loc.T("Write ss instead of ß in German output."))
        ], preferences => preferences.GermanOutputVariant.ToString(),
            (preferences, id) => preferences with { GermanOutputVariant = Enum.Parse<GermanOutputVariant>(id) },
            Loc.T("Applied after snippets and dictionary corrections. Requires German to be detected or selected as the spoken language; automatic language without detection leaves spelling unchanged."));
        RefreshAll();
    }
}
