using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class WorkflowsView
{
    private ContentDialog? _defaultsDialog;
    private TaskCompletionSource? _defaultsCompletion;
    internal event Action? DefaultsSaved;
    private WorkflowLlmSelection? ReadDefaults()
    {
        try { return _session?.WorkflowDefaults.Read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
    private (string Provider, string Model) EffectiveSelection(string provider, string model)
    {
        if (provider != WorkflowLlmDefaults.Inherit) return (provider, model);
        var defaults = ReadDefaults();
        return (defaults?.Provider ?? "none", defaults?.Model ?? "");
    }
    private bool EffectiveAvailable(string provider, string model)
    {
        var effective = EffectiveSelection(provider, model);
        return Available(effective.Provider, effective.Model);
    }
    private string? EffectiveConfigurationError(string provider, string model)
    {
        var effective = EffectiveSelection(provider, model);
        var error = ManualWorkflowRunner.ConfigurationError(effective.Provider, effective.Model, Available);
        return error is null ? null : provider == WorkflowLlmDefaults.Inherit
            ? Loc.T("The default LLM is missing or unavailable. Open Default LLM to choose a provider and model, or select your own provider for this workflow.")
            : error;
    }
    private void UpdateExecutionSummary()
    {
        if (_opened is null) return;
        var action = _session?.PluginRuntime.Actions.FirstOrDefault(a => a.PluginId == _opened.TargetActionPluginId);
        var destination = string.IsNullOrWhiteSpace(_opened.TargetActionPluginId) ? ""
            : "\n" + Loc.T("After processing: {0}. The result is sent there instead of being pasted.", action?.Name ?? Loc.T("Saved action unavailable"));
        var activation = _opened.TriggerKind == TypeWhisper.Core.Models.WorkflowTriggerKind.Hotkey
            ? "\n" + (_opened.HotkeyBehavior == TypeWhisper.Core.Models.WorkflowHotkeyBehavior.StartDictation
                ? Loc.T("Shortcut: {0} — press in another app to start dictation, then press again to stop and run the action.", _opened.Hotkeys)
                : Loc.T("Shortcut: {0} — process selected text.", _opened.Hotkeys))
            : _opened.TriggerKind == TypeWhisper.Core.Models.WorkflowTriggerKind.Manual
                ? "\n" + Loc.T("Manual: enter text below and press the action button. The normal dictation shortcut does not run this workflow.") : "";
        var recordingTask = _opened.SelectedTask switch
        {
            "transcribe" => "\n" + Loc.T("Recording task: Transcribe."),
            "translate" => "\n" + Loc.T("Recording task: Translate to English using a compatible transcription model."),
            _ => ""
        };
        if (_opened.Template == TypeWhisper.Core.Models.WorkflowTemplate.Dictation) { WorkflowExecutionSummary.Text = Loc.T("No LLM processing") + recordingTask + destination + activation; return; }
        var choice = EffectiveSelection(_opened.ProviderId, _opened.ModelId);
        WorkflowExecutionSummary.Text = EffectiveConfigurationError(_opened.ProviderId, _opened.ModelId)
            ?? (_opened.ProviderId == WorkflowLlmDefaults.Inherit
                ? Loc.T("Default LLM: {0} \u00b7 {1} \u00b7 input is sent to this provider when you run", Providers.FirstOrDefault(p => p.Id == choice.Provider)?.Label ?? choice.Provider, choice.Model)
                : Loc.T("{0} \u00b7 {1} \u00b7 input is sent to this provider when you run", Providers.FirstOrDefault(p => p.Id == choice.Provider)?.Label ?? choice.Provider, choice.Model));
        var memory = string.IsNullOrWhiteSpace(_opened.MemoryPluginId) ? "" : "\n" + Loc.T("Memory context: {0} · matching saved facts are sent to this provider.",
            _session?.PluginRuntime.MemoryProviders.FirstOrDefault(p => p.PluginId == _opened.MemoryPluginId)?.Name ?? Loc.T("Saved source unavailable"));
        WorkflowExecutionSummary.Text += recordingTask + memory + destination + activation;
    }
    private async void DefaultLlm_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _session is null || _defaultsDialog is not null || _run is not null || _deleteCompletion is { Task.IsCompleted: false }) return;
        var completion = _defaultsCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ChoicePicker();
        var model = new ChoicePicker();
        provider.Configure(Loc.T("Provider"), "plugin", Loc.T("Default workflow LLM provider"));
        model.Configure(Loc.T("Model"), "chip", Loc.T("Default workflow LLM model"));
        var saved = ReadDefaults();
        provider.SetOptions(Providers.Where(p => p.Id != WorkflowLlmDefaults.Inherit).ToArray(), saved?.Provider ?? "none");
        void RefreshModels(string selected) => model.SetOptions(
            _session.LlmProviders.FirstOrDefault(p => p.SelectionId == provider.SelectedId)?.Models
                .Select(m => new Choice(m.Id, m.DisplayName, m.Id)).ToArray() ?? [], selected, Loc.T("Choose a model"));
        RefreshModels(saved?.Model ?? "");
        provider.SelectionChanged += _ => RefreshModels("");
        var help = SettingsHelp.Label(Loc.T("Default workflow LLM"), Loc.T("Used by workflows set to Use default. Existing custom selections stay unchanged."));
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 16, MinWidth = 300 };
        content.Children.Add(help); content.Children.Add(provider); content.Children.Add(model); content.Children.Add(message);
        var dialog = _defaultsDialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = Loc.T("Default workflow LLM"), Content = content,
            PrimaryButtonText = Loc.T("Save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (ManualWorkflowRunner.ConfigurationError(provider.SelectedId, model.SelectedId, Available) is { } error)
                    throw new InvalidOperationException(error);
                _session.WorkflowDefaults.Save(new(provider.SelectedId, model.SelectedId));
                DefaultsSaved?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { args.Cancel = true; message.Text = ex.Message; message.Visibility = Visibility.Visible; }
        };
        try { await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { WorkflowSummary.Text = Loc.T("Default LLM settings could not open: {0}", ex.Message); }
        finally
        {
            _defaultsDialog = null;
            if (!_closing)
            {
                if (_page == Page.Configuration) { ConfigureModels(ConfigModel.SelectedId); UpdateConfigurationState(); }
                else { UpdateExecutionSummary(); UpdateSourceState(); }
            }
            completion.TrySetResult();
        }
    }
}
