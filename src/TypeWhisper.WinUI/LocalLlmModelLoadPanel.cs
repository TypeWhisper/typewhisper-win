using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

// Below an LLM model picker: a downloaded local text model that is not loaded yet loads when it is chosen,
// with its progress and result here. The owner refreshes its pickers on Changed.
internal sealed class LocalLlmModelLoadPanel : StackPanel
{
    private readonly Func<LocalDictationSession?> _session;
    private readonly Func<PortableLlmProvider?> _provider;
    private readonly Func<string> _selectedModel;
    private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed };
    private readonly HandCursorButton _action = new() { HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _load;
    // The last result stays until another model or provider is chosen.
    private (string Provider, string Model, string Message, bool Failed)? _result;

    internal LocalLlmModelLoadPanel(Func<LocalDictationSession?> session, Func<PortableLlmProvider?> provider, Func<string> selectedModel)
    {
        _session = session; _provider = provider; _selectedModel = selectedModel;
        Spacing = 8;
        Margin = new Thickness(0, 10, 0, 0);
        Visibility = Visibility.Collapsed;
        _action.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, Loc.T("Model loading progress"));
        Children.Add(_message); Children.Add(_progress); Children.Add(_action);
        _action.Click += async (_, _) => { if (_load is { } running) running.Cancel(); else await LoadSelectedAsync(); };
    }

    internal bool IsLoading => _load is not null;
    /// <summary>Raised when a load starts or ends.</summary>
    internal event Action? Changed;

    /// <summary>The provider's models, followed by its downloaded models that load when chosen.</summary>
    internal static Choice[] Choices(PortableLlmProvider? provider) => provider is null ? [] :
    [
        .. provider.Models.Select(model => new Choice(model.Id, model.DisplayName, model.Id)),
        .. provider.LoadableModels.Select(model => new Choice(model.Id, model.DisplayName, Loc.T("Downloaded · loads when you choose it")))
    ];

    /// <summary>The provider picker's note: a local provider without a loaded model says how to get one.</summary>
    internal static string ProviderDescription(PortableLlmProvider provider) => provider.Ready ? Loc.T("Ready")
        : !provider.ManagesLocalModels ? Loc.T("Requires configuration")
        : provider.LoadableModels.Count > 0 ? Loc.T("Choose a downloaded model to load it")
        : Loc.T("Download a model in its plugin settings");

    /// <summary>Called when the user chose a model: a downloaded one that is not loaded loads right away.</summary>
    internal async Task SelectionChangedAsync()
    {
        _result = null;
        if (Loadable() is not null) await LoadSelectedAsync();
        else Update();
    }

    /// <summary>Cancels a running load, for example when the dialog or page closes.</summary>
    internal void Cancel() => _load?.Cancel();

    /// <summary>Forgets the last result, for example when another workflow opens.</summary>
    internal void Reset() => _result = null;

    /// <summary>Shows what the current provider and model need; a running load keeps its progress.</summary>
    internal void Update()
    {
        if (IsLoading) return;
        var provider = _provider();
        var selected = _selectedModel();
        if (_result is { } result && (result.Provider != provider?.SelectionId || result.Model != selected)) _result = null;
        _progress.Visibility = Visibility.Collapsed;
        if (_result is { } last)
        {
            Show(last.Message, last.Failed, last.Failed && Loadable() is not null ? Loc.T("Try again") : null);
            return;
        }
        if (Loadable() is { } model)
        {
            // A saved selection that is not loaded, for example after another model was loaded.
            Show(Loc.T("{0} is downloaded but not loaded.", model.DisplayName), false, Loc.T("Load model"));
            return;
        }
        if (provider is { ManagesLocalModels: true, Models.Count: 0, LoadableModels.Count: 0 })
        {
            Show(Loc.T("No model is downloaded yet. Download one in the {0} plugin settings; it loads right after the download.", provider.Name), false, null);
            return;
        }
        Visibility = Visibility.Collapsed;
    }

    private PluginModelInfo? Loadable()
    {
        var selected = _selectedModel();
        return _provider()?.LoadableModels.FirstOrDefault(model => model.Id == selected);
    }

    private async Task LoadSelectedAsync()
    {
        if (IsLoading || _session() is not { } session || _provider() is not { } provider || Loadable() is not { } model) { Update(); return; }
        using var load = _load = new CancellationTokenSource();
        _result = null;
        Show(Loc.T("Loading {0} into memory…", model.DisplayName), false, Loc.T("Cancel"));
        _progress.Visibility = Visibility.Visible;
        Changed?.Invoke();
        string message;
        var failed = true;
        try
        {
            await session.LoadLocalLlmModelAsync(provider.PluginId, model.Id, model.DisplayName, load.Token);
            message = Loc.T("{0} is loaded and ready.", model.DisplayName);
            failed = false;
        }
        catch (OperationCanceledException) { message = Loc.T("Loading {0} was cancelled.", model.DisplayName); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { message = Loc.T("{0} could not be loaded: {1}", model.DisplayName, ex.Message); }
        finally { _load = null; }
        _result = (provider.SelectionId, model.Id, message, failed);
        Changed?.Invoke();
        Update();
    }

    private void Show(string message, bool failed, string? action)
    {
        Visibility = Visibility.Visible;
        _message.Text = message;
        _message.Foreground = (Brush)Application.Current.Resources[failed ? "AccentBrush" : "MutedBrush"];
        _action.Content = action ?? "";
        _action.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
