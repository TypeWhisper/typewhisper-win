using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveSpokenFormattingSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = SettingsRow.Require(content, "SpokenFormattingProfiles").Reset(pickers);
        var language = new ChoicePicker(); language.Configure(Loc.T("Profile language"), "language", Loc.T("Spoken formatting profile language"));
        var strategy = new ChoicePicker(); strategy.Configure(Loc.T("Formatting strategy"), "text", Loc.T("Spoken formatting strategy"));
        var both = new StackPanel { Spacing = 6, Width = 260 };
        language.UseRowHeight(); strategy.UseRowHeight();
        both.Children.Add(language); both.Children.Add(strategy); pickers.Add(language); pickers.Add(strategy);
        // The description names the profile being edited.
        row.Set(Loc.T("Spoken formatting"), "",
            Loc.T("Local rules are available for English and German. A profile applies only to its engine, model and language. Automatic language needs a recognized language; native translation uses the English profile."), both);

        var appToggle = AppToggleSwitch.Create(session.TextPreferences.Current.AppFormattingEnabled);
        AutomationProperties.SetName(appToggle, Loc.T("Markdown bullets in supported apps"));
        var appRow = new SettingsRow("AppFormattingEnabled").Set(Loc.T("Markdown bullets in supported apps"),
            Loc.T("In Obsidian, Notion, MarkText, Typora and Bear, convert lines starting with “bullet ” to Markdown list items. Other output stays unchanged. Uses the app where recording started."), appToggle);
        row.InsertAfter(content, appRow);
        var selectedLanguage = SpokenFormattingLanguageNormalizer.Normalize(session.Language) is "de" ? "de" : "en";
        string? displayedEngine = null;
        string? displayedModel = null;
        var restoring = false;
        void Refresh()
        {
            var preferences = session.TextPreferences.Current;
            var engine = session.ActiveEngineId;
            var model = session.ActiveModelId;
            displayedEngine = engine; displayedModel = model;
            var selectedTask = session.TranscriptionTaskPreferences.Current;
            if (selectedTask == TranscriptionTask.Translate) selectedLanguage = "en";
            language.SetOptions(DictationFormatting.SupportedLanguages.Order().Select(code => new Choice(code,
                code == "de" ? Loc.T("German") : Loc.T("English"), Loc.T("Profile language"))).ToArray(), selectedLanguage);
            var profile = string.IsNullOrWhiteSpace(engine) ? null : DictationFormatting.Resolve(preferences, engine, model, selectedLanguage, null);
            strategy.SetOptions([
                new("default", Loc.T("Engine defaults"), Loc.T("Keep the engine's output. No local spoken-command replacements.")),
                new("nativeOnly", Loc.T("Keep engine output"), Loc.T("Explicitly disable local spoken-command replacements for this profile.")),
                new("automatic", Loc.T("Replace spoken commands"), Loc.T("Replace visible commands such as “new line” and adjust nearby spacing.")),
                new("fallbackOnly", Loc.T("Commands and spacing"), Loc.T("Replace visible commands and normalize spacing throughout the text."))
            ], profile?.Profile.StrategyOverride?.ToRawValue() ?? "default");
            var configured = SpokenFormattingLanguageNormalizer.Normalize(session.Language);
            row.Description = string.IsNullOrWhiteSpace(engine) || string.IsNullOrWhiteSpace(model)
                ? Loc.T("Select an active dictation model to configure its profiles.")
                : Loc.T("{0} · {1} · {2} profile", session.ActiveModelName, engine, selectedLanguage.ToUpperInvariant());
            strategy.IsEnabled = !string.IsNullOrWhiteSpace(engine) && !string.IsNullOrWhiteSpace(model);
            language.IsEnabled = strategy.IsEnabled && selectedTask != TranscriptionTask.Translate;
            var unknown = profile?.Profile.StrategyOverrideRaw is { Length: > 0 } && profile?.Profile.StrategyOverride is null;
            row.Status = session.TextPreferences.Error ?? (unknown
                ? Loc.T("This profile has an unknown strategy from another version. Engine output is retained until you choose a supported strategy.")
                : selectedTask != TranscriptionTask.Translate && configured is not null && !DictationFormatting.SupportedLanguages.Contains(configured)
                    ? Loc.T("The current spoken language has no local rules. This saved English/German profile applies when that language is selected or detected.")
                    : "");
            restoring = true; appToggle.IsOn = preferences.AppFormattingEnabled; restoring = false;
            appRow.Status = session.TextPreferences.Error ?? "";
        }
        language.SelectionChanged += id => { selectedLanguage = id; Refresh(); };
        strategy.SelectionChanged += id =>
        {
            if (displayedEngine is not { Length: > 0 } engine) return;
            if (engine != session.ActiveEngineId || displayedModel != session.ActiveModelId)
            {
                Refresh(); row.Status = Loc.T("The active model changed. Review its profile before changing the strategy."); return;
            }
            var selected = SpokenFormattingStrategyValues.TryParse(id, out var value) ? value : (SpokenFormattingStrategy?)null;
            session.TextPreferences.Save(DictationFormatting.WithProfile(session.TextPreferences.Current, engine, displayedModel, selectedLanguage, selected));
            Refresh();
        };
        appToggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            session.TextPreferences.Save(session.TextPreferences.Current with { AppFormattingEnabled = appToggle.IsOn }); Refresh();
        };
        void OnChanged() => row.DispatcherQueue.TryEnqueue(() => { if (row.IsLoaded) Refresh(); });
        row.Loaded += (_, _) => { session.Changed += OnChanged; session.Models.Changed += OnChanged; session.PluginRuntime.Changed += OnChanged; Refresh(); };
        row.Unloaded += (_, _) => { session.Changed -= OnChanged; session.Models.Changed -= OnChanged; session.PluginRuntime.Changed -= OnChanged; };
        Refresh();
    }
}
