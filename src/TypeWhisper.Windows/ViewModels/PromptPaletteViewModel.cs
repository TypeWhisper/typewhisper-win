using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.ViewModels;

public partial class PromptPaletteViewModel : ObservableObject
{
    private readonly IPromptActionService _promptActions;
    private readonly PromptProcessingService _processing;
    private readonly TextInsertionService _textInsertion;
    private readonly PluginManager _pluginManager;
    private readonly InputSimulator _inputSimulator = new();

    private bool _opening;

    [ObservableProperty] private bool _isOpen;

    public PromptPaletteViewModel(
        IPromptActionService promptActions,
        PromptProcessingService processing,
        TextInsertionService textInsertion,
        PluginManager pluginManager)
    {
        _promptActions = promptActions;
        _processing = processing;
        _textInsertion = textInsertion;
        _pluginManager = pluginManager;
    }

    public async void TogglePalette()
    {
        if (_opening || IsOpen) return;

        _opening = true;
        try
        {
            await OpenPaletteAsync();
        }
        finally
        {
            _opening = false;
        }
    }

    private async Task OpenPaletteAsync()
    {
        var actions = _promptActions.EnabledActions;
        if (actions.Count == 0) return;

        // Capture selected text before opening window
        var capturedText = await CaptureSelectedTextAsync();

        // Show the palette window as a modal dialog
        var window = new PromptPaletteWindow();
        window.SourceText = capturedText;
        window.SetActions(actions);

        IsOpen = true;
        var result = window.ShowDialog();
        IsOpen = false;

        if (result == true && window.SelectedAction is { } action)
        {
            await ExecuteActionAsync(action, capturedText);
        }
    }

    private async Task ExecuteActionAsync(PromptAction action, string capturedText)
    {
        if (string.IsNullOrWhiteSpace(capturedText))
            return;

        if (!_processing.IsAnyProviderAvailable)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await _processing.ProcessAsync(action, capturedText, cts.Token);

            // Route to action plugin if configured
            if (!string.IsNullOrEmpty(action.TargetActionPluginId))
            {
                var actionPlugin = _pluginManager.ActionPlugins
                    .FirstOrDefault(p => p.PluginId == action.TargetActionPluginId
                                      || p.ActionId == action.TargetActionPluginId);

                if (actionPlugin is not null)
                {
                    var context = new ActionContext(null, null, null, null, capturedText);
                    await actionPlugin.ExecuteAsync(result, context, cts.Token);
                    return;
                }
            }

            await _textInsertion.InsertTextAsync(result, autoPaste: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Prompt processing error: {ex.Message}");
        }
    }

    private async Task<string> CaptureSelectedTextAsync()
    {
        string? previousClipboard = null;
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Clipboard.ContainsText())
                    previousClipboard = Clipboard.GetText();
                Clipboard.Clear();
            });
        }
        catch { /* clipboard locked */ }

        _inputSimulator.Keyboard.ModifiedKeyStroke(
            VirtualKeyCode.CONTROL, VirtualKeyCode.VK_C);

        await Task.Delay(150);

        var selectedText = "";
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Clipboard.ContainsText())
                    selectedText = Clipboard.GetText();
            });
        }
        catch { /* clipboard locked */ }

        // Restore previous clipboard
        if (previousClipboard is not null)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    Clipboard.SetText(previousClipboard));
            }
            catch { /* best effort */ }
        }

        return selectedText;
    }
}
