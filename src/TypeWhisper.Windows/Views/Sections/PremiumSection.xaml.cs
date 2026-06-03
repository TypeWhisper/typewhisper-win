using System.IO;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class PremiumSection : UserControl
{
    public PremiumSection()
    {
        InitializeComponent();
    }

    private SettingsWindowViewModel? ViewModel => DataContext as SettingsWindowViewModel;

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select sync folder"
        };

        var current = ViewModel?.CloudFolderSync.SelectedFolderPath;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == true)
            ViewModel?.CloudFolderSync.SetFolderPath(dialog.FolderName);
    }

    private void OnOpenLicense(object sender, RoutedEventArgs e)
    {
        ViewModel?.Open(SettingsRoute.License);
    }
}
