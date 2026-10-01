using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Runtime binding stays separate from the remaining preview settings catalog.
internal sealed class LiveDictationSettings(LocalDictationSession session, Action<string> openPluginSettings)
{
    private static string LanguageName(string code)
    {
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }
    internal void Configure(string category, StackPanel content, List<ChoicePicker> pickers)
    {
        LiveRecorderSettings.Configure(category, content, pickers, session.RecorderPreferences, session.GetRecorderOutputDevices);
        LiveOutputSettings.Configure(category, content, pickers, session);
        LiveHistoryRetentionSettings.Configure(category, content, pickers, session.HistoryRetention);
        LiveRecordingModeSettings.Configure(category, content, pickers, session);
        LiveTextProcessingSettings.Configure(category, content, pickers, session);
        LiveLanguageHintSettings.Configure(category, content, pickers, session);
        LiveSpokenFormattingSettings.Configure(category, content, pickers, session);
        LiveTranscriptionTaskSettings.Configure(category, content, pickers, session);
        if (category == "Audio")
        {
            pickers.Clear();
            new LiveAudioSettings(session).Render(content, pickers);
        }
        if (category == "Dictation")
        {
            var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "DictationModel"));
            row.Children.Clear();
            var provider = new ChoicePicker();
            provider.Configure(Loc.T("Provider"), "plugin", Loc.T("Dictation provider"));
            row.Children.Add(new TextBlock { Text = Loc.T("Provider"), FontSize = 14 });
            row.Children.Add(provider); pickers.Add(provider);
            var modelSection = new StackPanel { Spacing = 8 };
            var model = new ChoicePicker();
            model.Configure(Loc.T("Model"), "chip", Loc.T("Active dictation model"));
            modelSection.Children.Add(new TextBlock { Text = Loc.T("Model"), FontSize = 14 });
            modelSection.Children.Add(model); pickers.Add(model);
            row.Children.Add(modelSection);
            var hint = new TextBlock { FontSize = 12, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
            row.Children.Add(hint);
            var setup = new HandCursorButton { Content = Loc.T("Provider settings"), HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryButtonStyle"] };
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
                provider.SetOptions(providers.Select(item => new Choice(item.Id, item.Name,
                    (item.Cloud ? Loc.T("Cloud") : Loc.T("On-device")) + " · " + item.Status) { PluginId = item.PluginId }).ToArray(), selectedProviderId, Loc.T("Choose a provider"));
                var canChange = session.CanChangeProvider && !session.Models.Busy && !selecting;
                provider.IsEnabled = canChange;
                modelSection.Visibility = selected?.Models.Count > 1 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
                model.SetOptions(selected?.Models.Select(item => new Choice(item.Id, item.Name,
                    item.Ready ? Loc.T("Ready") : Loc.T("Download in provider settings"), item.Ready)).ToArray() ?? [], selected?.SelectedModelId ?? "", Loc.T("Choose a model"));
                model.IsEnabled = canChange && selected?.Enabled == true && selected.Models.Any(item => item.Ready);
                setup.IsEnabled = !selecting && selected is not null;
                setup.Content = selected?.Ready != true ? Loc.T("Set up provider") : selected.Id != session.ActiveProviderId ? Loc.T("Use provider") : Loc.T("Provider settings");
                hint.Text = selectionError ?? (selected is null ? Loc.T("Set up a transcription provider in Integrations.")
                    : !selected.Ready ? Loc.T("{0}. Open provider settings to finish setup. Active: {1}.", selected.Status, session.ActiveModelName)
                    : selected.Id != session.ActiveProviderId ? Loc.T("Ready. Select this provider to use it for dictation.")
                    : selected.Cloud ? session.SupportsLiveTranscription
                        ? Loc.T("With live text enabled, audio is streamed to this provider during recording.")
                        : Loc.T("Recorded audio is sent to this provider after recording. Live preview is unavailable.")
                    : Loc.T("Audio is transcribed on this device."));
            });
            row.Loaded += (_, _) => { session.Models.Changed += Refresh; session.Changed += Refresh; Refresh(); };
            row.Unloaded += (_, _) => { session.Models.Changed -= Refresh; session.Changed -= Refresh; };
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
            var language = new ChoicePicker();
            language.Configure(Loc.T("Spoken language"), "language", Loc.T("Dictation language"));
            void RefreshLanguage() => languageRow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!languageRow.IsLoaded) return;
                var options = session.SupportedLanguages.Select(code => new Choice(code,
                    LanguageName(code), Loc.T("Supported by the active model"))).ToArray();
                language.SetOptions(options.Length == 0 || session.UsesRegistryProvider ? new Choice[] { new("auto", Loc.T("Automatic"), Loc.T("Language detection by the model")) }.Concat(options).ToArray() : options, options.Length == 0 ? "auto" : session.Language);
                language.IsEnabled = selectedProviderId == session.ActiveProviderId && session.CanChangeProvider && (session.UsesRegistryProvider ? session.IsReady : session.CanSelectModel) && options.Length > 0;
            });
            languageRow.Loaded += (_, _) => { session.Models.Changed += RefreshLanguage; session.Changed += RefreshLanguage; RefreshLanguage(); };
            languageRow.Unloaded += (_, _) => { session.Models.Changed -= RefreshLanguage; session.Changed -= RefreshLanguage; };
            language.SelectionChanged += id => { var error = session.SelectLanguage(id); RefreshLanguage(); if (error is not null) hint.Text = error; };
            languageRow.Children.Add(new TextBlock { Text = Loc.T("Spoken language"), FontSize = 14 });
            languageRow.Children.Add(language); pickers.Add(language);
            provider.SelectionChanged += _ => RefreshLanguage();
        }
    }
}
