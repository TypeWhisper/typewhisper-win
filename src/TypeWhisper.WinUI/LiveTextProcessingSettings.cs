using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveTextProcessingSettings
{
    internal static void Configure(string category, StackPanel content, List<PrototypeChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var store = session.TextPreferences;
        var refreshers = new List<Action>();
        void RefreshAll() { foreach (var refresh in refreshers) refresh(); }

        StackPanel Prepare(string key)
        {
            var row = FindRow(content, key) ?? throw new InvalidOperationException($"Text settings row '{key}' is missing.");
            foreach (var old in row.Children.OfType<PrototypeChoicePicker>()) pickers.Remove(old);
            row.Children.Clear();
            return row;
        }

        void AddToggle(string key, string title, string description, Func<DictationTextPreferences, bool> get,
            Func<DictationTextPreferences, bool, DictationTextPreferences> update)
        {
            var row = Prepare(key);
            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var toggle = PrototypeToggleSwitch.Create(get(store.Current));
            AutomationProperties.SetName(toggle, title);
            AutomationProperties.SetHelpText(toggle, description + " Changes apply to the next recording.");
            header.Children.Add(Label(title, 14));
            Grid.SetColumn(toggle, 1); header.Children.Add(toggle);
            row.Children.Add(header);
            row.Children.Add(Label(description));
            var status = Label(""); row.Children.Add(status);
            var restoring = false;
            refreshers.Add(() =>
            {
                restoring = true;
                toggle.IsOn = get(store.Current);
                restoring = false;
                status.Text = store.Error ?? "Saved for the next recording.";
            });
            toggle.Toggled += (_, _) =>
            {
                if (restoring) return;
                store.Save(update(store.Current, toggle.IsOn));
                RefreshAll();
            };
        }

        void AddChoice(string key, string title, IReadOnlyList<PrototypeChoice> options,
            Func<DictationTextPreferences, string> get, Func<DictationTextPreferences, string, DictationTextPreferences> update,
            string description)
        {
            var row = Prepare(key);
            row.Children.Add(Label(title, 14));
            var picker = new PrototypeChoicePicker();
            picker.Configure(title, "language", "Preference " + key);
            row.Children.Add(picker); pickers.Add(picker);
            row.Children.Add(Label(description));
            var status = Label(""); row.Children.Add(status);
            refreshers.Add(() =>
            {
                picker.SetOptions(options, get(store.Current));
                status.Text = store.Error ?? "Saved for the next recording.";
            });
            picker.SelectionChanged += id =>
            {
                if (options.Any(option => option.Id == id)) store.Save(update(store.Current, id));
                RefreshAll();
            };
        }

        AddToggle("TranscriptionNumberNormalizationEnabled", "Normalize numbers",
            "Write spoken numbers as digits where appropriate for the transcript language.",
            preferences => preferences.TranscriptionNumberNormalizationEnabled,
            (preferences, enabled) => preferences with { TranscriptionNumberNormalizationEnabled = enabled });
        AddToggle("ShortUtterancePunctuationEnabled", "Keep punctuation in short phrases",
            "Keep model punctuation in one- or two-word phrases. When off, remove greeting commas and ending punctuation from these phrases.",
            preferences => preferences.ShortUtterancePunctuationEnabled,
            (preferences, enabled) => preferences with { ShortUtterancePunctuationEnabled = enabled });
        AddChoice("EnglishOutputVariant", "English spelling", [
            new("AsTranscribed", "As transcribed", "Keep spelling from transcription and corrections."),
            new("UnitedStates", "American", "Apply American spelling to recognized English output."),
            new("UnitedKingdom", "British", "Apply British spelling to recognized English output.")
        ], preferences => preferences.EnglishOutputVariant.ToString(),
            (preferences, id) => preferences with { EnglishOutputVariant = Enum.Parse<EnglishOutputVariant>(id) },
            "Applied after snippets and dictionary corrections. Requires English to be detected or selected as the spoken language; automatic language without detection leaves spelling unchanged.");
        AddChoice("GermanOutputVariant", "German spelling", [
            new("AsTranscribed", "As transcribed", "Keep spelling from transcription and corrections."),
            new("Switzerland", "Swiss Standard German", "Write ss instead of ß in German output.")
        ], preferences => preferences.GermanOutputVariant.ToString(),
            (preferences, id) => preferences with { GermanOutputVariant = Enum.Parse<GermanOutputVariant>(id) },
            "Applied after snippets and dictionary corrections. Requires German to be detected or selected as the spoken language; automatic language without detection leaves spelling unchanged.");
        RefreshAll();
    }

    private static StackPanel? FindRow(StackPanel root, string key)
    {
        if (Equals(root.Tag, key)) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindRow(child, key) is { } row) return row;
        return null;
    }

    private static TextBlock Label(string text, double size = 12) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources[size > 12 ? "TextBrush" : "MutedBrush"]
    };
}
