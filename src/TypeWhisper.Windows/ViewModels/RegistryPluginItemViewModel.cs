using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides registry plugin item view model behavior.
/// </summary>
public partial class RegistryPluginItemViewModel : ObservableObject
{
    private readonly RegistryPlugin _registryPlugin;
    private readonly PluginRegistryService _registryService;

    /// <summary>
    /// Gets the id.
    /// </summary>
    public string Id => _registryPlugin.Id;
    /// <summary>
    /// Gets the display or storage name.
    /// </summary>
    public string Name => _registryPlugin.Name;
    /// <summary>
    /// Gets the version.
    /// </summary>
    public string Version => _registryPlugin.Version;
    /// <summary>
    /// Gets the author.
    /// </summary>
    public string Author => _registryPlugin.Author;
    /// <summary>
    /// Gets the description.
    /// </summary>
    public string Description => _registryPlugin.Description;
    /// <summary>
    /// Gets the category.
    /// </summary>
    public string? Category => _registryPlugin.Category;
    /// <summary>
    /// Gets the categories.
    /// </summary>
    public IReadOnlyList<string>? Categories => _registryPlugin.Categories;
    /// <summary>
    /// Gets the requires api key.
    /// </summary>
    public bool RequiresApiKey => _registryPlugin.RequiresApiKey;
    /// <summary>
    /// Performs format size.
    /// </summary>
    public string SizeDisplay => FormatSize(_registryPlugin.Size);
    /// <summary>
    /// Gets the logo path.
    /// </summary>
    public string? LogoPath => PluginIconHelper.GetLogoPath(Id);
    /// <summary>
    /// Gets whether has logo.
    /// </summary>
    public bool HasLogo => LogoPath is not null;
    /// <summary>
    /// Performs icon emoji.
    /// </summary>
    public string IconEmoji => PluginIconHelper.GetIcon(Id);
    /// <summary>
    /// Performs icon gradient start.
    /// </summary>
    public string IconGradientStart => PluginIconHelper.GetGradientStart(Id);
    /// <summary>
    /// Performs icon gradient end.
    /// </summary>
    public string IconGradientEnd => PluginIconHelper.GetGradientEnd(Id);
    /// <summary>
    /// Gets the category descriptors.
    /// </summary>
    public IReadOnlyList<PluginMarketplaceCategoryDescriptor> CategoryDescriptors =>
        PluginMarketplaceCategories.ResolveAll(Category, Categories);
    /// <summary>
    /// Performs category keys.
    /// </summary>
    public IReadOnlyList<string> CategoryKeys => CategoryDescriptors.Select(category => category.Key).ToArray();
    /// <summary>
    /// Gets the category key.
    /// </summary>
    public string CategoryKey => CategoryDescriptors[0].Key;
    /// <summary>
    /// Gets the category label.
    /// </summary>
    public string CategoryLabel => CategoryDescriptors[0].DisplayName;
    /// <summary>
    /// Gets the category sort order.
    /// </summary>
    public int CategorySortOrder => CategoryDescriptors[0].SortOrder;
    /// <summary>
    /// Gets the location badge.
    /// </summary>
    public string LocationBadge => RequiresApiKey ? Loc.Instance["Plugins.Cloud"] : Loc.Instance["Plugins.Local"];

    [ObservableProperty] private PluginInstallState _installState;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _installErrorMessage = "";

    /// <summary>
    /// Returns whether install error.
    /// </summary>
    public bool HasInstallError => !string.IsNullOrWhiteSpace(InstallErrorMessage);
    /// <summary>
    /// Gets whether the plugin is installed or has an installed update available.
    /// </summary>
    public bool IsInstalledOrUpdateAvailable =>
        InstallState is PluginInstallState.Installed or PluginInstallState.UpdateAvailable;

    /// <summary>
    /// Initializes a new instance of the RegistryPluginItemViewModel class.
    /// </summary>
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
            var result = await _registryService.InstallPluginAsync(_registryPlugin, progressReporter);
            InstallState = result == PluginInstallResult.PendingRestart
                ? PluginInstallState.PendingRestart
                : PluginInstallState.Installed;
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
            var result = await _registryService.InstallPluginAsync(_registryPlugin, progressReporter);
            InstallState = result == PluginInstallResult.PendingRestart
                ? PluginInstallState.PendingRestart
                : PluginInstallState.Installed;
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

    partial void OnInstallStateChanged(PluginInstallState value)
    {
        OnPropertyChanged(nameof(IsInstalledOrUpdateAvailable));
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

/// <summary>
/// Represents plugin marketplace category descriptor data.
/// </summary>
/// <param name="Key">Key supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
/// <param name="SortOrder">Sort order supplied to the member.</param>
public sealed record PluginMarketplaceCategoryDescriptor(string Key, string DisplayName, int SortOrder);

/// <summary>
/// Provides plugin marketplace categories behavior.
/// </summary>
public static class PluginMarketplaceCategories
{
    /// <summary>
    /// Resolves the supplied input to a configured value.
    /// </summary>
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

    /// <summary>
    /// Resolves all.
    /// </summary>
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
