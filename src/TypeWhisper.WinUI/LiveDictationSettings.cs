using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Runtime binding stays separate from the remaining prototype settings catalog.
internal sealed class LiveDictationSettings(LocalDictationSession session, Action<string> openPluginSettings)
{
    private static string LanguageName(string code)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }
    internal void Configure(string category, StackPanel content, List<PrototypeChoicePicker> pickers)
    {
        if (category == "Audio")
        {
            pickers.Clear();
            new LiveAudioSettings(session).Render(content, pickers);
        }
        if (category == "Dictation")
        {
            var previewNote = content.Children.OfType<TextBlock>().FirstOrDefault(text => text.Text.StartsWith("Preview only"));
            if (previewNote is not null) previewNote.Text = "Model and language are saved. Other options on this page may still be previews.";
            var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "DictationModel"));
            row.Children.Clear();
            var provider = new PrototypeChoicePicker();
            provider.Configure("Provider", "plugin", "Dictation provider");
            row.Children.Add(new TextBlock { Text = "Provider", FontSize = 14 });
            row.Children.Add(provider); pickers.Add(provider);
            var modelSection = new StackPanel { Spacing = 8 };
            var model = new PrototypeChoicePicker();
            model.Configure("Model", "chip", "Active dictation model");
            modelSection.Children.Add(new TextBlock { Text = "Model", FontSize = 14 });
            modelSection.Children.Add(model); pickers.Add(model);
            row.Children.Add(modelSection);
            var hint = new TextBlock { FontSize = 12, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
            row.Children.Add(hint);
            var setup = new HandCursorButton { Content = "Provider settings", HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
            row.Children.Add(setup);
            var selectedProviderId = session.ActiveProviderId;
            var observedActiveProviderId = session.ActiveProviderId;
            string? selectionError = null;
            bool selecting = false;
            void Refresh() => row.DispatcherQueue.TryEnqueue(() =>
            {
                if (!row.IsLoaded) return;
                if (observedActiveProviderId != session.ActiveProviderId)
                    selectedProviderId = observedActiveProviderId = session.ActiveProviderId;
                var providers = session.DictationProviders;
                var selected = providers.FirstOrDefault(item => item.Id == selectedProviderId);
                provider.SetOptions(providers.Select(item => new PrototypeChoice(item.Id, item.Name,
                    (item.Cloud ? "Cloud" : "On-device") + " · " + item.Status)).ToArray(), selectedProviderId, "Choose a provider");
                var canChange = session.CanChangeProvider && !session.Models.Busy && !selecting;
                provider.IsEnabled = canChange;
                modelSection.Visibility = selected?.Models.Count > 1 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
                model.SetOptions(selected?.Models.Select(item => new PrototypeChoice(item.Id, item.Name,
                    item.Ready ? "Ready" : "Download in provider settings", item.Ready)).ToArray() ?? [], selected?.SelectedModelId ?? "", "Choose a model");
                model.IsEnabled = canChange && selected?.Ready == true;
                setup.IsEnabled = !selecting && selected is not null;
                setup.Content = selected?.Ready != true ? "Set up provider" : selected.Id != session.ActiveProviderId ? "Use provider" : "Provider settings";
                hint.Text = selectionError ?? (selected is null ? "Set up a transcription provider in Integrations."
                    : !selected.Ready ? selected.Status + ". Open provider settings to finish setup. Active: " + session.ActiveModelName + "."
                    : selected.Id != session.ActiveProviderId ? "Ready. Select this provider to use it for dictation."
                    : selected.Cloud ? "Recorded audio is sent to this provider after recording. Live preview is unavailable."
                    : "Audio is transcribed on this device.");
            });
            row.Loaded += (_, _) => { session.Models.Changed += Refresh; session.Groq.Changed += Refresh; session.Changed += Refresh; Refresh(); };
            row.Unloaded += (_, _) => { session.Models.Changed -= Refresh; session.Groq.Changed -= Refresh; session.Changed -= Refresh; };
            async Task Select(string providerId, string modelId)
            {
                selecting = true; selectionError = null; Refresh();
                try { selectionError = await session.SelectProviderModelAsync(providerId, modelId); }
                finally { selecting = false; selectedProviderId = session.ActiveProviderId; Refresh(); }
            }
            provider.SelectionChanged += async id =>
            {
                selectedProviderId = id; selectionError = null;
                var selected = session.DictationProviders.FirstOrDefault(item => item.Id == id);
                if (selected?.PreferredModelId is { } modelId) await Select(id, modelId);
                else Refresh();
            };
            model.SelectionChanged += async id => await Select(selectedProviderId, id);
            setup.Click += async (_, _) =>
            {
                var selected = session.DictationProviders.FirstOrDefault(item => item.Id == selectedProviderId);
                if (selected?.Ready == true && selected.Id != session.ActiveProviderId && selected.PreferredModelId is { } modelId)
                    await Select(selected.Id, modelId);
                else if (selected is not null) openPluginSettings(selected.PluginId);
            };
            var languageRow = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "Language"));
            languageRow.Children.Clear();
            var language = new PrototypeChoicePicker();
            language.Configure("Spoken language", "language", "Dictation language");
            void RefreshLanguage() => languageRow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!languageRow.IsLoaded) return;
                var options = session.SupportedLanguages.Select(code => new PrototypeChoice(code,
                    LanguageName(code), "Supported by the active model")).ToArray();
                language.SetOptions(options.Length == 0 || session.UsesGroq ? new PrototypeChoice[] { new("auto", "Automatic", "Language detection by the model") }.Concat(options).ToArray() : options, session.Language);
                language.IsEnabled = selectedProviderId == session.ActiveProviderId && session.CanChangeProvider && (session.UsesGroq ? session.Groq.Ready : session.CanSelectModel) && options.Length > 0;
            });
            languageRow.Loaded += (_, _) => { session.Models.Changed += RefreshLanguage; session.Groq.Changed += RefreshLanguage; session.Changed += RefreshLanguage; RefreshLanguage(); };
            languageRow.Unloaded += (_, _) => { session.Models.Changed -= RefreshLanguage; session.Groq.Changed -= RefreshLanguage; session.Changed -= RefreshLanguage; };
            language.SelectionChanged += id => { var error = session.SelectLanguage(id); RefreshLanguage(); if (error is not null) hint.Text = error; };
            languageRow.Children.Add(new TextBlock { Text = "Spoken language", FontSize = 14 });
            languageRow.Children.Add(language); pickers.Add(language);
            provider.SelectionChanged += _ => RefreshLanguage();
        }
    }
}
