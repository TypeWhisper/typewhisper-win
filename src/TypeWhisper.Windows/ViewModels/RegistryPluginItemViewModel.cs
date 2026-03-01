using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

public partial class RegistryPluginItemViewModel : ObservableObject
{
    private readonly RegistryPlugin _registryPlugin;
    private readonly PluginRegistryService _registryService;

    public string Id => _registryPlugin.Id;
    public string Name => _registryPlugin.Name;
    public string Version => _registryPlugin.Version;
    public string Author => _registryPlugin.Author;
    public string Description => _registryPlugin.Description;
    public string? Category => _registryPlugin.Category;
    public bool RequiresApiKey => _registryPlugin.RequiresApiKey;
    public string SizeDisplay => FormatSize(_registryPlugin.Size);
    public string IconEmoji => PluginIconHelper.GetIcon(Id);
    public string IconGradientStart => PluginIconHelper.GetGradientStart(Id);
    public string IconGradientEnd => PluginIconHelper.GetGradientEnd(Id);

    [ObservableProperty] private PluginInstallState _installState;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWorking;

    public RegistryPluginItemViewModel(RegistryPlugin registryPlugin, PluginRegistryService registryService)
    {
        _registryPlugin = registryPlugin;
        _registryService = registryService;
        _installState = registryService.GetInstallState(registryPlugin);
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsWorking) return;

        IsWorking = true;
        Progress = 0;

        try
        {
            var progressReporter = new Progress<double>(p => Progress = p);
            await _registryService.InstallPluginAsync(_registryPlugin, progressReporter);
            InstallState = PluginInstallState.Installed;
        }
        catch
        {
            InstallState = PluginInstallState.NotInstalled;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (IsWorking) return;

        IsWorking = true;

        try
        {
            await _registryService.UninstallPluginAsync(_registryPlugin.Id);
            InstallState = PluginInstallState.NotInstalled;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (IsWorking) return;

        IsWorking = true;
        Progress = 0;

        try
        {
            var progressReporter = new Progress<double>(p => Progress = p);
            await _registryService.InstallPluginAsync(_registryPlugin, progressReporter);
            InstallState = PluginInstallState.Installed;
        }
        catch
        {
            InstallState = PluginInstallState.UpdateAvailable;
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
