using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Runtime binding stays separate from the remaining preview settings catalog.
internal sealed class LiveDictationSettings(LocalDictationSession session, Action<string> openPluginSettings)
{
    internal static string LanguageName(string code)
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
            var row = SettingsRow.Require(content, "DictationModel");
            var provider = new ChoicePicker();
            provider.Configure(Loc.T("Provider"), "plugin", Loc.T("Dictation provider"));
            pickers.Add(provider);
            var setup = new HandCursorButton { Content = Loc.T("Provider settings"),
                Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryButtonStyle"] };
            provider.Width = 240; provider.UseRowHeight();
            var providerControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            providerControls.Children.Add(setup); providerControls.Children.Add(provider);
            row.Set(Loc.T("Provider"), control: providerControls);
            var model = new ChoicePicker();
            model.Configure(Loc.T("Model"), "chip", Loc.T("Active dictation model"));
            pickers.Add(model);
            var modelSection = new SettingsRow().Set(Loc.T("Model"), control: model);
            row.InsertAfter(content, modelSection);
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
                row.Description = selectionError ?? (selected is null ? Loc.T("Set up a transcription provider in Integrations.")
                    : !selected.Ready ? Loc.T("{0}. Open provider settings to finish setup. Active: {1}.", selected.Status, session.ActiveModelName)
                    : selected.Id != session.ActiveProviderId ? Loc.T("Ready. Select this provider to use it for dictation.")
                    : selected.Cloud ? session.SupportsLiveTranscription
                        ? Loc.T("With live text enabled, audio is streamed to this provider during recording.")
                        : Loc.T("Recorded audio is sent to this provider after recording. Live preview is unavailable.")
                    : Loc.T("Audio is transcribed on this device."));
            });
            ViewSubscriptions.Attach(row, () => { session.Models.Changed += Refresh; session.Changed += Refresh; Refresh(); },
                () => { session.Models.Changed -= Refresh; session.Changed -= Refresh; });
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
            var languageRow = SettingsRow.Require(content, "Language").Reset(pickers);
            var language = new ChoicePicker();
            language.Configure(Loc.T("Spoken language"), "language", Loc.T("Dictation language"));
            void RefreshLanguage() => languageRow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!languageRow.IsLoaded) return;
                var detects = session.SupportedLanguages.Count == 0;
                var options = session.LanguageChoices.Select(code => new Choice(code, LanguageName(code),
                    detects ? Loc.T("Used for spoken commands and formatting; the model still detects the language") : Loc.T("Supported by the active model")))
                    .OrderBy(choice => detects ? choice.Label : "", StringComparer.CurrentCulture).ToArray();
                language.SetOptions(detects || session.UsesRegistryProvider ? new Choice[] { new("auto", Loc.T("Automatic"), Loc.T("Language detection by the model")) }.Concat(options).ToArray() : options, options.Length == 0 ? "auto" : session.Language);
                language.IsEnabled = selectedProviderId == session.ActiveProviderId && session.CanChangeProvider && (session.UsesRegistryProvider ? session.IsReady : session.CanSelectModel) && options.Length > 0;
            });
            ViewSubscriptions.Attach(languageRow, () => { session.Models.Changed += RefreshLanguage; session.Changed += RefreshLanguage; RefreshLanguage(); },
                () => { session.Models.Changed -= RefreshLanguage; session.Changed -= RefreshLanguage; });
            language.SelectionChanged += id => { var error = session.SelectLanguage(id); RefreshLanguage(); languageRow.Status = error ?? ""; };
            languageRow.Set(Loc.T("Spoken language"), control: language); pickers.Add(language);
            provider.SelectionChanged += _ => RefreshLanguage();
        }
    }
}
