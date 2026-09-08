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
        var row = FindRow(content, "SpokenFormattingProfiles") ?? throw new InvalidOperationException("Spoken formatting row is missing.");
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.Children.Add(Label("Spoken formatting", 14));
        var context = Label(""); row.Children.Add(context);
        var language = new ChoicePicker(); language.Configure("Profile language", "language", "Spoken formatting profile language");
        var strategy = new ChoicePicker(); strategy.Configure("Formatting strategy", "text", "Spoken formatting strategy");
        row.Children.Add(language); row.Children.Add(strategy); pickers.Add(language); pickers.Add(strategy);
        row.Children.Add(Label("Local rules are available for English and German. A profile applies only to its engine, model and language. Automatic language needs a recognized language; native translation uses the English profile."));
        var status = Label(""); row.Children.Add(status);

        var appRow = new Grid { ColumnSpacing = 12, Tag = "AppFormattingEnabled" };
        appRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        appRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        appRow.Children.Add(Label("Markdown bullets in supported apps", 14));
        var appToggle = AppToggleSwitch.Create(session.TextPreferences.Current.AppFormattingEnabled);
        AutomationProperties.SetName(appToggle, "Markdown bullets in supported apps");
        Grid.SetColumn(appToggle, 1); appRow.Children.Add(appToggle); row.Children.Add(appRow);
        row.Children.Add(Label("In Obsidian, Notion, MarkText, Typora and Bear, convert lines starting with “bullet ” to Markdown list items. Other output stays unchanged. Uses the app where recording started."));
        var appStatus = Label(""); row.Children.Add(appStatus);
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
                code == "de" ? "German" : "English", "Profile language")).ToArray(), selectedLanguage);
            var profile = string.IsNullOrWhiteSpace(engine) ? null : DictationFormatting.Resolve(preferences, engine, model, selectedLanguage, null);
            strategy.SetOptions([
                new("default", "Engine defaults", "Keep the engine's output. No local spoken-command replacements."),
                new("nativeOnly", "Keep engine output", "Explicitly disable local spoken-command replacements for this profile."),
                new("automatic", "Replace spoken commands", "Replace visible commands such as “new line” and adjust nearby spacing."),
                new("fallbackOnly", "Commands and spacing", "Replace visible commands and normalize spacing throughout the text.")
            ], profile?.Profile.StrategyOverride?.ToRawValue() ?? "default");
            var configured = SpokenFormattingLanguageNormalizer.Normalize(session.Language);
            context.Text = string.IsNullOrWhiteSpace(engine) || string.IsNullOrWhiteSpace(model)
                ? "Select an active dictation model to configure its profiles."
                : $"{session.ActiveModelName} · {engine} · {selectedLanguage.ToUpperInvariant()} profile";
            strategy.IsEnabled = !string.IsNullOrWhiteSpace(engine) && !string.IsNullOrWhiteSpace(model);
            language.IsEnabled = strategy.IsEnabled && selectedTask != TranscriptionTask.Translate;
            var unknown = profile?.Profile.StrategyOverrideRaw is { Length: > 0 } && profile?.Profile.StrategyOverride is null;
            status.Text = session.TextPreferences.Error ?? (unknown
                ? "This profile has an unknown strategy from another version. Engine output is retained until you choose a supported strategy."
                : selectedTask != TranscriptionTask.Translate && configured is not null && !DictationFormatting.SupportedLanguages.Contains(configured)
                    ? "The current spoken language has no local rules. This saved English/German profile applies when that language is selected or detected."
                    : "Saved for the next recording.");
            restoring = true; appToggle.IsOn = preferences.AppFormattingEnabled; restoring = false;
            appStatus.Text = session.TextPreferences.Error ?? "Saved for the next recording.";
        }
        language.SelectionChanged += id => { selectedLanguage = id; Refresh(); };
        strategy.SelectionChanged += id =>
        {
            if (displayedEngine is not { Length: > 0 } engine) return;
            if (engine != session.ActiveEngineId || displayedModel != session.ActiveModelId)
            {
                Refresh(); status.Text = "The active model changed. Review its profile before changing the strategy."; return;
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

    private static StackPanel? FindRow(StackPanel root, string key)
    {
        if (Equals(root.Tag, key)) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindRow(child, key) is { } row) return row;
        return null;
    }
    private static TextBlock Label(string text, double size = 12) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources[size > 12 ? "TextBrush" : "MutedBrush"]
    };
}
