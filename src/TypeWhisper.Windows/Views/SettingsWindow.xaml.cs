using System.Windows;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views.Sections;

namespace TypeWhisper.Windows.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RegisterSection("Dashboard", () => new DashboardSection { DataContext = viewModel });
        viewModel.RegisterSection("Allgemein", () => new GeneralSection { DataContext = viewModel });
        viewModel.RegisterSection("Aufnahme", () => new AudioSection { DataContext = viewModel });
        viewModel.RegisterSection("Modelle", () => new ModelsSection { DataContext = viewModel });
        viewModel.RegisterSection("Profile", () => new ProfilesSection { DataContext = viewModel });
        viewModel.RegisterSection("Wörterbuch", () => new DictionarySection { DataContext = viewModel });
        viewModel.RegisterSection("Snippets", () => new SnippetsSection { DataContext = viewModel });
        viewModel.RegisterSection("Verlauf", () => new HistorySection { DataContext = viewModel });
        viewModel.RegisterSection("Info", () => new InfoSection { DataContext = viewModel });

        viewModel.NavigateToDefault();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
