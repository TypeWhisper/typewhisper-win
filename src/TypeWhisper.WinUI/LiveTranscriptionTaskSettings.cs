using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.WinUI;

internal static class LiveTranscriptionTaskSettings
{
    internal static void Configure(string category, StackPanel content, List<PrototypeChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = FindRow(content, "TranscriptionTask") ?? throw new InvalidOperationException("Transcription task row is missing.");
        foreach (var old in row.Children.OfType<PrototypeChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.Children.Add(Label("Transcription task", 14));
        var picker = new PrototypeChoicePicker();
        picker.Configure("Transcription task", "language", "Preference TranscriptionTask");
        row.Children.Add(picker); pickers.Add(picker);
        var status = Label(""); row.Children.Add(status);
        string? selectionError = null;

        // Replace the preview-only target picker and detach it from its prototype
        // visibility rule, which reads a separate non-persistent values dictionary.
        if (FindRow(content, "TranslationTargetLanguage") is { } target)
        {
            foreach (var old in target.Children.OfType<PrototypeChoicePicker>()) pickers.Remove(old);
            RemoveRow(content, target);
            target.Children.Clear();
            target.Children.Add(Label("Translation language", 14));
            target.Children.Add(Label("English is the only native translation target. Translation to other languages is not available yet."));
            row.Children.Add(target);
        }

        void Refresh()
        {
            var selected = session.TranscriptionTaskPreferences.Current;
            picker.SetOptions([
                new("Transcribe", "Transcribe", "Write speech in its original language."),
                new("Translate", "Translate to English", "Use the active model's native audio-to-English translation.", session.SupportsTranslation)
            ], selected.ToString());
            picker.IsEnabled = session.CanChangeProvider;
            status.Text = selectionError ?? session.TranscriptionTaskPreferences.Error ??
                (selected == TranscriptionTask.Translate && !session.SupportsTranslation
                    ? "Translate to English is saved, but this model does not support it. Recording is blocked until you choose Transcribe or a compatible model."
                    : !session.CanChangeProvider
                        ? "Finish or cancel the current dictation before changing the task."
                        : session.SupportsTranslation
                            ? "Saved for this profile. Native translation produces English text."
                            : "This model supports transcription only. Select a translation-capable model to translate audio to English.");
        }
        void OnChanged() => row.DispatcherQueue.TryEnqueue(() => { if (row.IsLoaded) Refresh(); });
        picker.SelectionChanged += id =>
        {
            if (Enum.TryParse<TranscriptionTask>(id, out var task)) selectionError = session.SelectTranscriptionTask(task);
            Refresh();
        };
        row.Loaded += (_, _) =>
        {
            session.Changed += OnChanged; session.Models.Changed += OnChanged; session.Groq.Changed += OnChanged;
            Refresh();
        };
        row.Unloaded += (_, _) =>
        {
            session.Changed -= OnChanged; session.Models.Changed -= OnChanged; session.Groq.Changed -= OnChanged;
        };
        Refresh();
    }

    private static StackPanel? FindRow(StackPanel root, string key)
    {
        if (Equals(root.Tag, key)) return root;
        foreach (var child in root.Children.OfType<StackPanel>())
            if (FindRow(child, key) is { } row) return row;
        return null;
    }

    private static bool RemoveRow(StackPanel root, StackPanel target)
    {
        var index = root.Children.IndexOf(target);
        if (index >= 0) { root.Children.RemoveAt(index); return true; }
        foreach (var child in root.Children.OfType<StackPanel>())
            if (RemoveRow(child, target)) return true;
        return false;
    }

    private static TextBlock Label(string text, double size = 12) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[size > 12 ? "TextBrush" : "MutedBrush"]
    };
}
