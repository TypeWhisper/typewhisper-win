using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

internal static class LiveLanguageHintSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = FindRow(content) ?? throw new InvalidOperationException("Preferred language settings row is missing.");
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.Children.Add(SettingsHelp.Label("Preferred languages",
            "Choose up to two languages in preference order. Hints guide detection; they do not force an output language. Changes apply to the next recording."));
        var first = new ChoicePicker(); first.Configure("First language", "language", "First preferred language");
        var second = new ChoicePicker(); second.Configure("Second language", "language", "Second preferred language");
        row.Children.Add(first); row.Children.Add(second); pickers.Add(first); pickers.Add(second);
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }; row.Children.Add(hint);
        var restoring = false;
        void Refresh()
        {
            restoring = true;
            var codes = session.SupportedLanguages.Count > 0 ? session.SupportedLanguages : CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .Select(culture => culture.TwoLetterISOLanguageName).Where(code => code != "iv").Distinct().ToArray();
            var options = codes.Where(code => code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z'))
                .Select(code => new Choice(code, Name(code), "Preferred input language")).OrderBy(choice => choice.Label).ToList();
            var selected = session.TextPreferences.Current.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries);
            // Preserve saved choices visibly when switching to a provider with a narrower language list.
            foreach (var code in selected.Where(code => !options.Any(option => option.Id == code)))
                options.Add(new(code, Name(code) + " (unavailable)", "Not supported by this provider.", false));
            first.SetOptions(new[] { new Choice("", "Unrestricted", "Detect without preferred languages") }.Concat(options).ToArray(), selected.FirstOrDefault() ?? "");
            second.SetOptions(new[] { new Choice("", "None", "Use only the first preferred language") }.Concat(options.Where(option => option.Id != selected.FirstOrDefault())).ToArray(), selected.Skip(1).FirstOrDefault() ?? "");
            first.IsEnabled = session.SupportsLanguageHints && session.Language == "auto";
            second.IsEnabled = first.IsEnabled && selected.Length > 0;
            hint.Text = session.TextPreferences.Error ?? (!session.SupportsLanguageHints
                ? "The selected model does not support multiple language hints. Saved preferences remain available for compatible models."
                : session.Language != "auto" ? "Your explicit spoken language takes precedence. Choose Automatic to use preferred languages."
                : "Saved for the next recording.");
            restoring = false;
        }
        void Save(bool primary, string code)
        {
            if (restoring) return;
            var saved = session.TextPreferences.Current.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var values = primary ? new[] { code, saved.Skip(1).FirstOrDefault() ?? "" } : new[] { saved.FirstOrDefault() ?? "", code };
            session.TextPreferences.Save(session.TextPreferences.Current with
            { PreferredLanguageHints = primary && code == "" ? "" : string.Join(',', values.Where(value => value.Length > 0).Distinct()) });
            Refresh();
        }
        first.SelectionChanged += code => Save(true, code);
        second.SelectionChanged += code => Save(false, code);
        void Changed() { if (row.DispatcherQueue.HasThreadAccess) Refresh(); else row.DispatcherQueue.TryEnqueue(Refresh); }
        row.Loaded += (_, _) => { session.Changed += Changed; Refresh(); };
        row.Unloaded += (_, _) => session.Changed -= Changed;
        Refresh();
    }

    private static string Name(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).EnglishName; } catch (CultureNotFoundException) { return code; }
    }
    private static StackPanel? FindRow(StackPanel root)
    {
        if (Equals(root.Tag, "LanguageHints")) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindRow(child) is { } row) return row;
        return null;
    }
}
