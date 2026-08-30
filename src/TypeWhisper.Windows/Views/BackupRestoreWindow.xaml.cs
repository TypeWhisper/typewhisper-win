using System.Windows;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

/// <summary>Shows a validated backup preview and the detailed restore result.</summary>
public partial class BackupRestoreWindow : Window
{
    private readonly BackupRestoreViewModel _viewModel;

    /// <summary>Initializes the restore dialog with an already validated preview.</summary>
    public BackupRestoreWindow(BackupRestoreViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _viewModel.SelectAll();

    private void SelectNone_Click(object sender, RoutedEventArgs e) => _viewModel.SelectNone();

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            Loc.Instance["Backup.RestoreConfirmMessage"],
            Loc.Instance["Backup.RestoreConfirmTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            await _viewModel.RestoreAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.Instance.GetString("Backup.RestoreFailedFormat", ex.Message),
                Loc.Instance["Backup.RestoreFailedTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
