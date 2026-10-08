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
    // What a run of this workflow uses and where its result goes.
    private string ExecutionSummary(WorkflowDraft workflow)
    {
        var action = _session?.PluginRuntime.Actions.FirstOrDefault(a => a.PluginId == workflow.TargetActionPluginId);
        var destination = string.IsNullOrWhiteSpace(workflow.TargetActionPluginId) ? ""
            : "\n" + Loc.T("After processing: {0}. The result is sent there instead of being pasted.", action?.Name ?? Loc.T("Saved action unavailable"));
        var recordingTask = workflow.SelectedTask switch
        {
            "transcribe" => "\n" + Loc.T("Recording task: Transcribe."),
            "translate" => "\n" + Loc.T("Recording task: Translate to English using a compatible transcription model."),
            _ => ""
        };
        if (workflow.Template == TypeWhisper.Core.Models.WorkflowTemplate.Dictation) return Loc.T("No LLM processing") + recordingTask + destination;
        var choice = EffectiveSelection(workflow.ProviderId, workflow.ModelId);
        var provider = EffectiveConfigurationError(workflow.ProviderId, workflow.ModelId)
            ?? (workflow.ProviderId == WorkflowLlmDefaults.Inherit
                ? Loc.T("Default LLM: {0} · {1} · input is sent to this provider when you run", Providers.FirstOrDefault(p => p.Id == choice.Provider)?.Label ?? choice.Provider, choice.Model)
                : Loc.T("{0} · {1} · input is sent to this provider when you run", Providers.FirstOrDefault(p => p.Id == choice.Provider)?.Label ?? choice.Provider, choice.Model));
        var memory = string.IsNullOrWhiteSpace(workflow.MemoryPluginId) ? "" : "\n" + Loc.T("Memory context: {0} · matching saved facts are sent to this provider.",
            _session?.PluginRuntime.MemoryProviders.FirstOrDefault(p => p.PluginId == workflow.MemoryPluginId)?.Name ?? Loc.T("Saved source unavailable"));
        return provider + recordingTask + memory + destination;
    }
    private async void DefaultLlm_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _session is null || _defaultsDialog is not null || _testDialog is not null || _run is not null || _deleteCompletion is { Task.IsCompleted: false }) return;
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
            Title = Loc.T("Default workflow LLM"), Content = content,
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
        try { await Dialogs.ShowAsync(this, dialog); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { WorkflowSummary.Text = Loc.T("Default LLM settings could not open: {0}", ex.Message); }
        finally
        {
            _defaultsDialog = null;
            if (!_closing)
            {
                if (_page == Page.Configuration) { ConfigureModels(ConfigModel.SelectedId); UpdateConfigurationState(); }
            }
            completion.TrySetResult();
        }
    }
}
