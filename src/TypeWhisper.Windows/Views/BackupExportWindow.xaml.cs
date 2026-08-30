using System.Windows;
using Microsoft.Win32;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

/// <summary>Lets the user select categories and write a portable TypeWhisper backup.</summary>
public partial class BackupExportWindow : Window
{
    private readonly BackupRestoreViewModel _viewModel;

    /// <summary>Initializes the export dialog.</summary>
    public BackupExportWindow(BackupRestoreViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.PrepareExportAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.Instance.GetString("Backup.ExportFailedFormat", ex.Message),
                Loc.Instance["Backup.ExportFailedTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _viewModel.SelectAll();

    private void SelectNone_Click(object sender, RoutedEventArgs e) => _viewModel.SelectNone();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"typewhisper-backup-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = Loc.Instance["Backup.JsonFilter"]
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await _viewModel.ExportToFileAsync(dialog.FileName);
            MessageBox.Show(
                this,
                Loc.Instance.GetString("Backup.ExportSuccessFormat", dialog.FileName),
                Loc.Instance["Backup.ExportSuccessTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.Instance.GetString("Backup.ExportFailedFormat", ex.Message),
                Loc.Instance["Backup.ExportFailedTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
