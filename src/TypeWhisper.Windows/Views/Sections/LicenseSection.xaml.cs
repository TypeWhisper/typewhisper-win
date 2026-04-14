using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class LicenseSection : UserControl
{
    private readonly LicenseSectionViewModel _viewModel;

    public LicenseSection()
    {
        InitializeComponent();

        _viewModel = new LicenseSectionViewModel(
            App.Services.GetRequiredService<LicenseService>(),
            App.Services.GetRequiredService<SupporterDiscordService>());

        ContentRoot.DataContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private void OnCommercialLicenseKeyChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.CommercialLicenseKeyInput = CommercialLicenseKeyBox.Password;
    }

    private void OnSupporterLicenseKeyChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.SupporterLicenseKeyInput = SupporterLicenseKeyBox.Password;
    }
}
