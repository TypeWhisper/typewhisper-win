using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Runtime binding stays separate from the remaining prototype settings catalog.
internal sealed class LiveDictationSettings(LocalDictationSession session)
{
    internal void Configure(string category, StackPanel content, List<PrototypeChoicePicker> pickers)
    {
        if (category == "Audio")
        {
            pickers.Clear();
            new LiveAudioSettings(session).Render(content, pickers);
        }
        if (category == "Dictation")
        {
            var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "DictationModel"));
            row.Children.Clear();
            var model = new PrototypeChoicePicker();
            model.Configure("Model", "chip", "Active dictation model");
            model.SetOptions([new("parakeet", "Parakeet TDT 0.6B", "Installed local model used by real dictation")], "parakeet");
            model.IsEnabled = false;
            row.Children.Add(new TextBlock { Text = "Model", FontSize = 14 });
            row.Children.Add(model); pickers.Add(model);
            row.Children.Add(new TextBlock { Text = "Currently connected: Parakeet only. Additional model activation is not connected yet.", FontSize = 12, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
            var languageRow = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "Language"));
            languageRow.Children.Clear();
            var language = new PrototypeChoicePicker();
            language.Configure("Spoken language", "language", "Dictation language");
            language.SetOptions([new("automatic", "Automatic", "The connected Parakeet path does not expose a forced-language setting.")], "automatic");
            language.IsEnabled = false;
            languageRow.Children.Add(new TextBlock { Text = "Spoken language", FontSize = 14 });
            languageRow.Children.Add(language); pickers.Add(language);
            var advanced = content.Children.OfType<StackPanel>().Single(p => p.Tag is Action);
            var ctc = new StackPanel { Spacing = 8 };
            var switchRow = new Grid(); switchRow.ColumnDefinitions.Add(new()); switchRow.ColumnDefinitions.Add(new() { Width = Microsoft.UI.Xaml.GridLength.Auto });
            switchRow.Children.Add(new TextBlock { Text = "Experimental CTC vocabulary plugin", FontSize = 14, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center });
            var toggle = PrototypeToggleSwitch.Create(session.CtcVocabulary.Enabled);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Experimental CTC vocabulary plugin");
            Grid.SetColumn(toggle, 1); switchRow.Children.Add(toggle); ctc.Children.Add(switchRow);
            var hint = new TextBlock { Text = session.CtcVocabulary.Error ?? "Local English CTC model. Replaces text-based boosting; uses your words and Term packs. German vocabulary is not validated yet.",
                FontSize = 12, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["MutedBrush"] };
            ctc.Children.Add(hint);
            var restoring = false;
            toggle.Toggled += async (_, _) =>
            {
                if (restoring) return;
                toggle.IsEnabled = false;
                hint.Text = toggle.IsOn ? "Loading CTC model…" : "Finishing CTC requests and unloading…";
                try
                {
                    var error = session.IsRecording ? "Finish recording before changing CTC." : await session.CtcVocabulary.SetEnabledAsync(toggle.IsOn);
                    hint.Text = error ?? (session.CtcVocabulary.Enabled ? "CTC plugin ready · applies to the next dictation. English model; experimental." : "CTC disabled. The existing vocabulary boosting setting applies.");
                    restoring = true; toggle.IsOn = session.CtcVocabulary.Enabled; restoring = false;
                }
                finally { toggle.IsEnabled = true; }
            };
            advanced.Children.Add(ctc);
        }
    }
}
