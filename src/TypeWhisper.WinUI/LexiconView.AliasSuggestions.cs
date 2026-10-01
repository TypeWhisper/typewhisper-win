using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class LexiconView
{
    private ContentDialog? _aliasDialog;
    private CancellationTokenSource? _aliasCancellation;
    private Task? _aliasTask;
    private sealed record AliasModel(string ProviderId, string ModelId, string Label)
    {
        public override string ToString() => Label;
    }

    private void StartAliasSuggestions(string word = "")
    {
        if (_closing || _draft is not null || _trainingSession is null
            || _aliasTask is { IsCompleted: false } || _trainingTask is { IsCompleted: false }) return;
        _aliasTask = SuggestAliasesAsync(word);
    }

    private async Task SuggestAliasesAsync(string initialWord)
    {
        using var cancellation = _aliasCancellation = new CancellationTokenSource();
        Task request = Task.CompletedTask;
        var ended = false;
        var saved = false;
        var busy = false;
        var reviewedWord = "";
        var reviewed = false;
        var selected = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var word = Input(initialWord, Loc.T("Correct spelling"), false);
        word.MaxLength = 160;
        var language = new ComboBox
        {
            ItemsSource = new[] { "Deutsch", "English" },
            SelectedIndex = _trainingSession!.Language == "de" || System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "de" ? 0 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(language, Loc.T("Spoken language"));
        var models = _trainingSession.LlmProviders.Where(provider => provider.IsLocal && provider.Ready)
            .SelectMany(provider => provider.Models.Select(model => new AliasModel(provider.SelectionId, model.Id,
                provider.Name + " · " + model.DisplayName))).ToArray();
        var modelPicker = new ComboBox { ItemsSource = models, SelectedIndex = models.Length > 0 ? 0 : -1,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(modelPicker, Loc.T("Local language model"));
        var status = Text(models.Length == 0
            ? Loc.T("No local language model is ready. Enable Local LLM in Plugins, then download and load a model.")
            : "", 12, true);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(progress, Loc.T("Generating alias suggestions"));
        var results = new StackPanel { Spacing = 4 };
        var body = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 500 };
        body.Children.Add(Text(Loc.T("A local language model suggests how this term might be misheard. Select the variants you want to correct automatically in future dictations."), 13, true));
        body.Children.Add(Text(Loc.T("Correct spelling"), 12)); body.Children.Add(Surface(word, 2));
        body.Children.Add(Text(Loc.T("Spoken language"), 12)); body.Children.Add(language);
        body.Children.Add(Text(Loc.T("Local language model"), 12)); body.Children.Add(modelPicker);
        body.Children.Add(Text(Loc.T("Suggestions are generated on this device. Saved aliases appear under Corrections."), 12, true));
        body.Children.Add(progress); body.Children.Add(results); body.Children.Add(status);
        var dialog = _aliasDialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = Loc.T("Suggest misheard variants"),
            Content = new ScrollViewer { Content = body, MaxHeight = 460, Padding = new Thickness(0, 0, 16, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = Loc.T("Generate suggestions"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogBackground"] = Brush("InkBrush");
        dialog.Resources["ContentDialogTopOverlay"] = Brush("InkBrush");

        void UpdateButtons()
        {
            dialog.PrimaryButtonText = busy ? Loc.T("Generating…") : reviewed ? Loc.T("Save aliases") : Loc.T("Generate suggestions");
            dialog.IsPrimaryButtonEnabled = !busy && (reviewed
                ? selected.Values.Any(check => check.IsEnabled && check.IsChecked == true)
                : modelPicker.SelectedItem is AliasModel && DictionaryAliasSuggestions.IsTerm(word.Text.Trim()));
            dialog.SecondaryButtonText = reviewed ? Loc.T("Generate again") : "";
            dialog.IsSecondaryButtonEnabled = !busy;
            word.IsEnabled = language.IsEnabled = modelPicker.IsEnabled = !busy;
            progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        void ResetReview()
        {
            reviewed = false; selected.Clear(); results.Children.Clear();
            if (models.Length > 0) status.Text = "";
            UpdateButtons();
        }
        async Task GenerateAsync()
        {
            if (busy || ended || modelPicker.SelectedItem is not AliasModel model) return;
            reviewedWord = word.Text.Trim().Normalize();
            ResetReview(); busy = true; status.Text = Loc.T("Generating suggestions…"); UpdateButtons();
            try
            {
                var aliases = await _trainingSession.SuggestAliasesAsync(reviewedWord,
                    language.SelectedIndex == 0 ? "German" : "English", model.ProviderId, model.ModelId, cancellation.Token);
                if (ended || _closing || cancellation.IsCancellationRequested) return;
                _store.ReloadDictionary();
                reviewed = true;
                foreach (var alias in aliases)
                {
                    var existing = _store.Entries.FirstOrDefault(entry => entry.Kind == LexiconKind.Correction
                        && entry.Key.Equals(alias, StringComparison.OrdinalIgnoreCase));
                    var label = existing is null ? alias : existing.Value == reviewedWord
                        ? Loc.T("{0} · already in Dictionary", alias) : Loc.T("{0} · already corrects to {1}", alias, existing.Value);
                    var check = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                        IsChecked = false, IsEnabled = existing is null, HorizontalAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(check, label);
                    check.Checked += (_, _) => UpdateButtons();
                    check.Unchecked += (_, _) => UpdateButtons();
                    selected.Add(alias, check); results.Children.Add(check);
                }
                status.Text = aliases.Count == 0 ? Loc.T("No usable variants were found. Try generating again.")
                    : selected.Values.All(check => !check.IsEnabled) ? Loc.T("These variants already have corrections. No new aliases to add.")
                    : Loc.T("Select only variants that should always be replaced with “{0}”.", reviewedWord);
            }
            catch (OperationCanceledException)
            { if (!ended && !_closing) status.Text = Loc.T("Generation canceled. No aliases were added."); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!ended && !_closing) status.Text = ex is FormatException or InvalidOperationException or ArgumentException
                    ? ex.Message : Loc.T("Could not generate suggestions. Check the local model in Plugins and try again.");
                System.Diagnostics.Debug.WriteLine(ex);
            }
            finally { busy = false; if (!ended && !_closing) UpdateButtons(); }
        }
        word.TextChanged += (_, _) => ResetReview();
        language.SelectionChanged += (_, _) => ResetReview();
        modelPicker.SelectionChanged += (_, _) => ResetReview();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (busy || ended || _closing) return;
            if (!reviewed) { request = GenerateAsync(); return; }
            var aliases = selected.Where(item => item.Value.IsEnabled && item.Value.IsChecked == true).Select(item => item.Key).ToArray();
            var error = _store.SaveSuggestedAliases(reviewedWord, aliases);
            if (error is not null) { status.Text = error; return; }
            saved = true; _kind = LexiconKind.Correction; _query = reviewedWord;
            _expandedCorrections.Add(reviewedWord); args.Cancel = false;
        };
        dialog.SecondaryButtonClick += (_, args) => { args.Cancel = true; if (!busy && !ended) request = GenerateAsync(); };
        dialog.Closed += (_, _) => { ended = true; cancellation.Cancel(); };
        UpdateButtons();
        try { await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_closing) _notice.Text = Loc.T("Alias suggestions could not open: {0}", ex.Message); }
        finally
        {
            ended = true; cancellation.Cancel(); await request;
            _aliasDialog = null; _aliasCancellation = null;
            if (!_closing)
            {
                Render();
                _notice.Text = saved ? Loc.T("Aliases saved to Dictionary for the next dictation.") : Loc.T("No aliases were added.");
                _actions.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic);
            }
        }
    }
}
