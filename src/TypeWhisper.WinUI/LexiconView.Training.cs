using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

public sealed partial class LexiconView
{
    private LocalDictationSession? _trainingSession;
    private ContentDialog? _trainingDialog;
    private Task? _trainingTask;
    internal void ConnectTraining(LocalDictationSession session) => _trainingSession = session;

    private async Task TrainWordAsync()
    {
        if (_closing || _trainingSession is null) return;
        LocalDictationSession.TrainingCapture? capture = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var ended = false;
        try
        {
            await CorrectionLearning.Cancel();
            if (_closing) return;
            capture = _trainingSession.BeginWordTraining();
            var body = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 500 };
            var status = Text("", 12, true);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var wordInput = Input("", "Correct spelling", false);
            wordInput.MaxLength = 160;
            var exampleLanguage = new ComboBox
            {
                ItemsSource = new[] { "Deutsch", "English" },
                SelectedIndex = _trainingSession.Language == "de" || System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "de" ? 0 : 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(exampleLanguage, "Example sentence language");
            var dialog = _trainingDialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Train a word",
                Content = new ScrollViewer { Content = body, MaxHeight = 420, Padding = new Thickness(0, 0, 16, 0),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PrimaryButtonText = "Continue", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            };
            dialog.Resources["ContentDialogBackground"] = Brush("InkBrush");
            dialog.Resources["ContentDialogTopOverlay"] = Brush("InkBrush");
            var stage = 0;
            var index = 0;
            var word = "";
            string[] sentences = [];
            var transcripts = new string?[3];
            var approved = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
            var recording = false;
            var busy = false;
            var clock = new System.Diagnostics.Stopwatch();
            var meter = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4 };
            AutomationProperties.SetName(meter, "Microphone level");

            void Render()
            {
                if (ended || _closing) return;
                body.Children.Clear();
                dialog.IsPrimaryButtonEnabled = !busy;
                dialog.SecondaryButtonText = "";
                body.Children.Add(Text("Model: " + capture.ModelName, 12, true));
                if (stage == 0)
                {
                    body.Children.Add(Text("Speak three short sentences, then review the recognized variants before saving. This adds dictionary entries; it does not retrain the model.", 13, true));
                    body.Children.Add(Text("Correct spelling", 12));
                    body.Children.Add(Surface(wordInput, 2));
                    body.Children.Add(Text("Example sentence language", 12, true));
                    body.Children.Add(exampleLanguage);
                    body.Children.Add(Text("Uses your selected microphone and dictation model. Cloud models receive the sample audio. Training creates no History entry and does not paste text.", 12, true));
                    dialog.PrimaryButtonText = "Continue";
                }
                else if (stage == 1)
                {
                    dialog.Title = $"Train {word} · Sample {index + 1} of 3";
                    body.Children.Add(Text("Read this sentence aloud:", 12, true));
                    body.Children.Add(Text(sentences[index], 18));
                    if (recording) { body.Children.Add(meter); body.Children.Add(Text("Recording · stops after 30 seconds", 12, true)); }
                    if (transcripts[index] is { } transcript)
                    {
                        body.Children.Add(Text("Recognized:", 12, true));
                        body.Children.Add(Text(transcript, 14));
                        var candidate = DictionaryTrainingPlan.Candidate(word, sentences[index], transcript);
                        body.Children.Add(Text(candidate is not null ? $"Variant found: {candidate} → {word}"
                            : DictionaryTrainingPlan.Matches(sentences[index], transcript) ? "Correctly recognized."
                            : "The sentence differs in more than the target word. Record it again to get a useful variant.", 12, true));
                        dialog.PrimaryButtonText = index == 2 ? "Review" : "Next sample";
                        dialog.SecondaryButtonText = "Record again";
                    }
                    else dialog.PrimaryButtonText = busy ? "Transcribing…" : recording ? "Stop recording" : "Start recording";
                }
                else
                {
                    dialog.Title = $"Save training for {word}";
                    body.Children.Add(Text("The correct spelling will be saved under Words. Select the misheard variants to add under Corrections.", 13, true));
                    foreach (var item in approved.Values) body.Children.Add(item);
                    if (approved.Count == 0) body.Children.Add(Text("No unambiguous new variants were found. You can still save the word.", 13, true));
                    dialog.PrimaryButtonText = "Save to Dictionary";
                    dialog.SecondaryButtonText = "Review samples";
                }
                body.Children.Add(status);
            }
            async Task StopSampleAsync()
            {
                if (!recording || busy || ended) return;
                recording = false; busy = true; timer.Stop(); status.Text = ""; Render();
                try { transcripts[index] = await capture.StopAsync(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { if (!ended) status.Text = "Could not transcribe this sample. Check the microphone and provider, then try again."; System.Diagnostics.Debug.WriteLine(ex); }
                finally { busy = false; Render(); }
            }
            void Review()
            {
                stage = 2; approved.Clear(); _store.ReloadDictionary();
                for (var sample = 0; sample < 3; sample++)
                {
                    var candidate = DictionaryTrainingPlan.Candidate(word, sentences[sample], transcripts[sample] ?? "");
                    if (candidate is null || approved.ContainsKey(candidate)) continue;
                    var existing = _store.Entries.FirstOrDefault(entry => entry.Kind == LexiconKind.Correction &&
                        entry.Key.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                    var check = new CheckBox { Content = existing is null ? candidate :
                        existing.Value == word ? candidate + " · already in Dictionary" : candidate + " · already corrects to " + existing.Value,
                        IsChecked = existing is null, IsEnabled = existing is null };
                    approved.Add(candidate, check);
                }
                Render();
            }
            timer.Tick += async (_, _) =>
            {
                if (ended) return;
                meter.Value = capture.Level;
                if (clock.Elapsed.TotalSeconds >= 30) await StopSampleAsync();
            };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                if (busy || ended || _closing) return;
                if (stage == 0)
                {
                    word = wordInput.Text.Trim();
                    if (!DictionaryTrainingPlan.IsWord(word)) { status.Text = "Enter one word, using letters, numbers, apostrophes or hyphens."; return; }
                    sentences = DictionaryTrainingPlan.Sentences(word, exampleLanguage.SelectedIndex == 0);
                    capture.Language = exampleLanguage.SelectedIndex == 0 ? "de" : "en";
                    stage = 1; status.Text = ""; Render(); return;
                }
                if (stage == 1)
                {
                    if (recording) { await StopSampleAsync(); return; }
                    if (transcripts[index] is not null)
                    { status.Text = ""; if (index == 2) Review(); else { index++; Render(); } return; }
                    try { capture.Start(); recording = true; clock.Restart(); timer.Start(); status.Text = ""; }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = ex.Message; }
                    Render(); return;
                }
                var selected = approved.Where(item => item.Value.IsChecked == true && item.Value.IsEnabled).Select(item => item.Key).ToArray();
                var error = _store.SaveTraining(word, selected);
                if (error is not null) { status.Text = error; return; }
                _kind = selected.Length > 0 ? LexiconKind.Correction : LexiconKind.Word;
                _query = word;
                if (selected.Length > 0) _expandedCorrections.Add(word);
                args.Cancel = false;
            };
            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                if (busy || recording) return;
                status.Text = "";
                if (stage == 2) { stage = 1; index = 0; }
                else
                {
                    transcripts[index] = null;
                    try { capture.Start(); recording = true; clock.Restart(); timer.Start(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = ex.Message; }
                }
                Render();
            };
            Render();
            var result = await dialog.ShowAsync();
            ended = true;
            if (!_closing)
            {
                RenderListAfterTraining(result == ContentDialogResult.Primary);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_closing) _notice.Text = "Word training unavailable: " + ex.Message; }
        finally
        {
            ended = true; timer.Stop(); _trainingDialog = null;
            if (capture is not null) await capture.DisposeAsync();
            if (!_closing) DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closing) _actions.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic);
            });
        }
    }

    private void RenderListAfterTraining(bool saved)
    {
        Render();
        _notice.Text = saved ? "Word training saved to Dictionary." : "Training canceled. No dictionary entries were added.";
    }
}
