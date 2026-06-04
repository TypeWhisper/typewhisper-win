using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides license section behavior.
/// </summary>
public partial class LicenseSection : UserControl
{
    private readonly LicenseSectionViewModel _viewModel;
    private bool _isInitialized;
    private bool _isViewModelAttached;

    /// <summary>
    /// Initializes a new instance of the LicenseSection class.
    /// </summary>
    public LicenseSection()
    {
        InitializeComponent();

        _viewModel = new LicenseSectionViewModel(
            App.Services.GetRequiredService<LicenseService>(),
            App.Services.GetRequiredService<SupporterDiscordService>());

        ContentRoot.DataContext = _viewModel;
        AttachViewModel();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        if (_isInitialized)
            return;

        try
        {
            await _viewModel.InitializeAsync();
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License section initialization failed: {ex.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void OnLicenseKeyChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.LicenseKeyInput = LicenseKeyBox.Password;
    }

    private void AttachViewModel()
    {
        if (_isViewModelAttached)
            return;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _isViewModelAttached = true;
    }

    private void DetachViewModel()
    {
        if (!_isViewModelAttached)
            return;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _isViewModelAttached = false;
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
