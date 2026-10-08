using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.WinUI;

internal static class LiveTranscriptionTaskSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        if (category != "Dictation") return;
        var row = SettingsRow.Require(content, "TranscriptionTask").Reset(pickers);
        var picker = new ChoicePicker();
        picker.Configure(Loc.T("Transcription task"), "language", "Preference TranscriptionTask");
        row.Set(Loc.T("Transcription task"),
            Loc.T("Transcribe writes speech in its original language. Native translation produces English text using a compatible model. The choice is saved for this profile."), picker);
        pickers.Add(picker);
        string? selectionError = null;

        // English is the only target, so the row states it instead of offering a choice.
        var target = SettingsRow.Require(content, "TranslationTargetLanguage").Reset(pickers);
        target.Set(Loc.T("Translation language"),
            Loc.T("English is the only native translation target. Translation to other languages is not available yet."),
            new TextBlock { Text = Loc.T("English"), FontSize = 13, Foreground = (Brush)Application.Current.Resources["MutedBrush"] });

        void Refresh()
        {
            var selected = session.TranscriptionTaskPreferences.Current;
            picker.SetOptions([
                new("Transcribe", Loc.T("Transcribe"), Loc.T("Write speech in its original language.")),
                new("Translate", Loc.T("Translate to English"), Loc.T("Use the active model's native audio-to-English translation."), session.SupportsTranslation)
            ], selected.ToString());
            picker.IsEnabled = session.CanChangeProvider;
            target.Visibility = selected == TranscriptionTask.Translate ? Visibility.Visible : Visibility.Collapsed;
            row.Status = selectionError ?? session.TranscriptionTaskPreferences.Error ??
                (selected == TranscriptionTask.Translate && !session.SupportsTranslation
                    ? Loc.T("Translate to English is saved, but this model does not support it. Recording is blocked until you choose Transcribe or a compatible model.")
                    : !session.CanChangeProvider
                        ? Loc.T("Finish or cancel the current dictation before changing the task.")
                        : session.SupportsTranslation
                            ? ""
                            : Loc.T("This model supports transcription only. Select a translation-capable model to translate audio to English."));
        }
        void OnChanged() => row.DispatcherQueue.TryEnqueue(() => { if (row.IsLoaded) Refresh(); });
        picker.SelectionChanged += id =>
        {
            if (Enum.TryParse<TranscriptionTask>(id, out var task)) selectionError = session.SelectTranscriptionTask(task);
            Refresh();
        };
        ViewSubscriptions.Attach(row, () =>
        {
            session.Changed += OnChanged; session.Models.Changed += OnChanged;
            Refresh();
        }, () =>
        {
            session.Changed -= OnChanged; session.Models.Changed -= OnChanged;
        });
        Refresh();
    }
}
