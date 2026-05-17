using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services.Localization;
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
    public IReadOnlyList<string>? Categories => _registryPlugin.Categories;
    public bool RequiresApiKey => _registryPlugin.RequiresApiKey;
    public string SizeDisplay => FormatSize(_registryPlugin.Size);
    public string? LogoPath => PluginIconHelper.GetLogoPath(Id);
    public bool HasLogo => LogoPath is not null;
    public string IconEmoji => PluginIconHelper.GetIcon(Id);
    public string IconGradientStart => PluginIconHelper.GetGradientStart(Id);
    public string IconGradientEnd => PluginIconHelper.GetGradientEnd(Id);
    public IReadOnlyList<PluginMarketplaceCategoryDescriptor> CategoryDescriptors =>
        PluginMarketplaceCategories.ResolveAll(Category, Categories);
    public IReadOnlyList<string> CategoryKeys => CategoryDescriptors.Select(category => category.Key).ToArray();
    public string CategoryKey => CategoryDescriptors[0].Key;
    public string CategoryLabel => CategoryDescriptors[0].DisplayName;
    public int CategorySortOrder => CategoryDescriptors[0].SortOrder;
    public string LocationBadge => RequiresApiKey ? Loc.Instance["Plugins.Cloud"] : Loc.Instance["Plugins.Local"];

    [ObservableProperty] private PluginInstallState _installState;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _installErrorMessage = "";

    public bool HasInstallError => !string.IsNullOrWhiteSpace(InstallErrorMessage);

    public RegistryPluginItemViewModel(RegistryPlugin registryPlugin, PluginRegistryService registryService)
    {
        _registryPlugin = registryPlugin;
        _registryService = registryService;
        _installState = registryService.GetInstallState(registryPlugin);
    }

    internal void RefreshInstallState()
    {
        InstallState = _registryService.GetInstallState(_registryPlugin);
        if (InstallState == PluginInstallState.Installed)
            InstallErrorMessage = "";
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsWorking) return;

        IsWorking = true;
        Progress = 0;
        InstallErrorMessage = "";

        try
        {
            var progressReporter = new Progress<double>(p => Progress = p);
            await _registryService.InstallPluginAsync(_registryPlugin, progressReporter);
            InstallState = PluginInstallState.Installed;
            Progress = 1;
        }
        catch (Exception ex)
        {
            InstallState = PluginInstallState.NotInstalled;
            InstallErrorMessage = Loc.Instance.GetString("Plugins.InstallFailedFormat", ex.Message);
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
        InstallErrorMessage = "";

        try
        {
            var progressReporter = new Progress<double>(p => Progress = p);
            await _registryService.InstallPluginAsync(_registryPlugin, progressReporter);
            InstallState = PluginInstallState.Installed;
            Progress = 1;
        }
        catch (Exception ex)
        {
            InstallState = PluginInstallState.UpdateAvailable;
            InstallErrorMessage = Loc.Instance.GetString("Plugins.UpdateFailedFormat", ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    partial void OnInstallErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasInstallError));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    internal void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(CategoryDescriptors));
        OnPropertyChanged(nameof(CategoryLabel));
        OnPropertyChanged(nameof(LocationBadge));
    }
}

public sealed record PluginMarketplaceCategoryDescriptor(string Key, string DisplayName, int SortOrder);

public static class PluginMarketplaceCategories
{
    public static PluginMarketplaceCategoryDescriptor Resolve(string? rawCategory) => Normalize(rawCategory) switch
    {
        "transcription" => new("transcription", Loc.Instance["Plugins.CategoryTranscription"], 0),
        "llm" => new("llm", Loc.Instance["Plugins.CategoryLlmProviders"], 1),
        "tts" => new("tts", Loc.Instance["Plugins.CategoryTts"], 2),
        "post-processing" => new("post-processing", Loc.Instance["Plugins.CategoryPostProcessors"], 3),
        "action" => new("action", Loc.Instance["Plugins.CategoryActions"], 4),
        "memory" => new("memory", Loc.Instance["Plugins.CategoryMemory"], 5),
        _ => new("utility", Loc.Instance["Plugins.CategoryUtilities"], 6)
    };

    public static IReadOnlyList<PluginMarketplaceCategoryDescriptor> ResolveAll(
        string? primaryCategory,
        IEnumerable<string>? categories)
    {
        var descriptors = new List<PluginMarketplaceCategoryDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Add(primaryCategory);
        if (categories is not null)
        {
            foreach (var category in categories)
                Add(category);
        }

        if (descriptors.Count == 0)
            descriptors.Add(Resolve("utility"));

        return descriptors;

        void Add(string? rawCategory)
        {
            if (string.IsNullOrWhiteSpace(rawCategory))
                return;

            var descriptor = Resolve(rawCategory);
            if (seen.Add(descriptor.Key))
                descriptors.Add(descriptor);
        }
    }

    private static string Normalize(string? rawCategory) => rawCategory?.Trim().ToLowerInvariant() switch
    {
        "transcription" => "transcription",
        "llm" => "llm",
        "tts" or "texttospeech" or "text-to-speech" or "text to speech" => "tts",
        "postprocessing" or "post-processing" or "postprocessor" or "post-processor" or "processing" => "post-processing",
        "action" => "action",
        "memory" => "memory",
        _ => "utility"
    };
}
