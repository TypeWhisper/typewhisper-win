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
        }
    }
}
