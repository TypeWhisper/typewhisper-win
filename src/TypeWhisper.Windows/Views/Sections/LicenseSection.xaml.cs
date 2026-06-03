using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
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
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnLicenseKeyChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.LicenseKeyInput = LicenseKeyBox.Password;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LicenseSectionViewModel.LicenseKeyInput) &&
            string.IsNullOrEmpty(_viewModel.LicenseKeyInput) &&
            !string.IsNullOrEmpty(LicenseKeyBox.Password))
        {
            LicenseKeyBox.Clear();
        }
    }
}
