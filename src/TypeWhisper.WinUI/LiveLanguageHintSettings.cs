using System.Globalization;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

internal static class LiveLanguageHintSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = SettingsRow.Require(content, "LanguageHints").Reset(pickers);
        var first = new ChoicePicker(); first.Configure(Loc.T("First language"), "language", Loc.T("First preferred language"));
        var second = new ChoicePicker(); second.Configure(Loc.T("Second language"), "language", Loc.T("Second preferred language"));
        var both = new StackPanel { Spacing = 6, Width = 260 };
        first.UseRowHeight(); second.UseRowHeight();
        both.Children.Add(first); both.Children.Add(second); pickers.Add(first); pickers.Add(second);
        row.Set(Loc.T("Preferred languages"),
            Loc.T("Choose up to two languages in preference order. Hints guide detection; they do not force an output language. Changes apply to the next recording."), both);
        var restoring = false;
        void Refresh()
        {
            restoring = true;
            var codes = session.SupportedLanguages.Count > 0 ? session.SupportedLanguages : CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .Select(culture => culture.TwoLetterISOLanguageName).Where(code => code != "iv").Distinct().ToArray();
            var options = codes.Where(code => code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z'))
                .Select(code => new Choice(code, Name(code), Loc.T("Preferred input language"))).OrderBy(choice => choice.Label).ToList();
            var selected = session.TextPreferences.Current.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries);
            // Preserve saved choices visibly when switching to a provider with a narrower language list.
            foreach (var code in selected.Where(code => !options.Any(option => option.Id == code)))
                options.Add(new(code, Loc.T("{0} (unavailable)", Name(code)), Loc.T("Not supported by this provider."), false));
            first.SetOptions(new[] { new Choice("", Loc.T("Unrestricted"), Loc.T("Detect without preferred languages")) }.Concat(options).ToArray(), selected.FirstOrDefault() ?? "");
            second.SetOptions(new[] { new Choice("", Loc.T("None"), Loc.T("Use only the first preferred language")) }.Concat(options.Where(option => option.Id != selected.FirstOrDefault())).ToArray(), selected.Skip(1).FirstOrDefault() ?? "");
            first.IsEnabled = session.SupportsLanguageHints && session.Language == "auto";
            second.IsEnabled = first.IsEnabled && selected.Length > 0;
            row.Status = session.TextPreferences.Error ?? (!session.SupportsLanguageHints
                ? Loc.T("The selected model does not support multiple language hints. Saved preferences remain available for compatible models.")
                : session.Language != "auto" ? Loc.T("Your explicit spoken language takes precedence. Choose Automatic to use preferred languages.")
                : "");
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
}
