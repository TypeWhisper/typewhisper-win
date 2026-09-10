using Microsoft.UI.Xaml;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

public sealed partial class SetupWizard
{
    private CancellationTokenSource? _pluginInstallation;

    private void AddPluginInstallation()
    {
        if (_session.Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) return;
        var status = Copy("Install NVIDIA Parakeet for local, offline dictation. Choose and download its model here afterwards.");
        _body.Children.Add(status);
        var install = Button("Install NVIDIA Parakeet", () => { });
        _body.Children.Add(install);
        install.Click += async (_, _) =>
        {
            if (_closing) return;
            if (_pluginInstallation is not null) { _pluginInstallation.Cancel(); return; }
            using var operation = new CancellationTokenSource();
            _pluginInstallation = operation;
            _selecting = true;
            install.Content = "Cancel installation";
            RefreshModelPickers(); RefreshStatus();
            try
            {
                var error = await _session.InstallSetupPluginAsync(new Progress<PluginInstallationProgress>(value =>
                {
                    if (!_closing && ReferenceEquals(_pluginInstallation, operation)) status.Text = value.Message;
                }), operation.Token);
                if (_closing) return;
                _feedback.ReportPersistence(error);
                if (error is null)
                {
                    _selectedProvider = "local";
                    install.Visibility = Visibility.Collapsed;
                    status.Visibility = Visibility.Collapsed;
                }
                else status.Text = error;
            }
            finally
            {
                _pluginInstallation = null;
                _selecting = false;
                if (!_closing) { install.Content = "Retry installation"; RefreshModelPickers(); RefreshStatus(); }
            }
        };
    }
}
