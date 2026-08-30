using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides advanced section behavior.
/// </summary>
public partial class AdvancedSection : UserControl
{
    /// <summary>
    /// Initializes a new instance of the AdvancedSection class.
    /// </summary>
    public AdvancedSection() => InitializeComponent();

    private void OnExportBackupClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel)
            return;

        var dialog = new BackupExportWindow(viewModel.BackupRestore)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel viewModel)
            return;

        var owner = Window.GetWindow(this);
        if (viewModel.Recorder.IsRecording ||
            viewModel.Recorder.IsTranscribing ||
            viewModel.FileTranscription.IsProcessing)
        {
            MessageBox.Show(
                owner,
                Loc.Instance["Backup.RestoreBusy"],
                Loc.Instance["Backup.RestoreTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var picker = new OpenFileDialog
        {
            DefaultExt = ".json",
            Filter = Loc.Instance["Backup.JsonFilter"],
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(owner) != true)
            return;

        if (!await viewModel.BackupRestore.PrepareImportAsync(picker.FileName))
        {
            MessageBox.Show(
                owner,
                Loc.Instance.GetString(
                    "Backup.InvalidFileFormat",
                    viewModel.BackupRestore.ErrorMessage),
                Loc.Instance["Backup.InvalidFileTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var dialog = new BackupRestoreWindow(viewModel.BackupRestore)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }

    private void OnTestSpokenFormattingClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel { SpokenFormatting.HasSelectedModel: true } viewModel)
            return;

        var dialog = new SpokenFormattingVerificationWindow(viewModel.SpokenFormatting)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
