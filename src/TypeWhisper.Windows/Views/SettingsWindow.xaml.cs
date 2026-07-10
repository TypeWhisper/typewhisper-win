using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views.Sections;
using Wpf.Ui.Controls;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Provides settings window behavior.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsWindowViewModel _viewModel;
    private WelcomeWindow? _setupWizard;

    /// <summary>
    /// Initializes a new instance of the SettingsWindow class.
    /// </summary>
    public SettingsWindow(SettingsWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.RegisterSection(SettingsRoute.Dashboard, () => new DashboardSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Dictation, () => new AudioSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Shortcuts, () => new ShortcutsSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.FileTranscription, () => new FileTranscriptionSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Recorder, () => new RecorderSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.History, () => new HistorySection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Dictionary, () => new DictionarySection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Snippets, () => new SnippetsSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Workflows, () => new WorkflowsSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Integrations, () => new PluginsSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.General, () => new GeneralSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Appearance, () => new AppearanceSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Advanced, () => new AdvancedSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.Premium, () => new PremiumSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.License, () => new LicenseSection { DataContext = viewModel });
        viewModel.RegisterSection(SettingsRoute.About, () => new InfoSection { DataContext = viewModel });

        viewModel.NavigateToDefault();

        _viewModel.SetupWizardRequested += OnSetupWizardRequested;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnSetupWizardRequested(object? sender, EventArgs e)
    {
        if (_setupWizard is { IsLoaded: true })
        {
            _setupWizard.Activate();
            return;
        }

        var wizard = App.Services.GetRequiredService<WelcomeWindow>();
        wizard.Owner = this;
        wizard.Closed += OnSetupWizardClosed;
        _setupWizard = wizard;
        wizard.Show();
    }

    private void OnSetupWizardClosed(object? sender, EventArgs e)
    {
        if (sender is WelcomeWindow wizard)
            wizard.Closed -= OnSetupWizardClosed;

        if (ReferenceEquals(sender, _setupWizard))
            _setupWizard = null;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.Settings.StopMicrophonePreview();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.SetupWizardRequested -= OnSetupWizardRequested;
        _setupWizard = null;
    }
}
