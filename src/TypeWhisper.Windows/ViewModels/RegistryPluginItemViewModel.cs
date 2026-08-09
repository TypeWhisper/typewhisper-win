using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
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
    private PluginInstallDiagnosis _diagnosis;
    private RegistryArtifactValidationResult _artifactTrust;
    private RegistryArtifactValidationResult _installedArtifactTrust;

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
    /// Gets the forward-compatible hosting metadata value.
    /// </summary>
    public string? Hosting => _registryPlugin.Hosting;
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
    public string LocationBadge => _registryPlugin.IsCloudHosted
        ? Loc.Instance["Plugins.Cloud"]
        : Loc.Instance["Plugins.Local"];
    /// <summary>
    /// Gets the verified source label for the current registry artifact.
    /// </summary>
    public string SourceBadge => GetSourceBadge(_artifactTrust, installed: false);
    /// <summary>
    /// Gets the stable source classification for Discover grouping.
    /// </summary>
    public RegistryArtifactSource ArtifactSource => _artifactTrust.Source;
    /// <summary>
    /// Gets the localized source group used by Discover.
    /// </summary>
    public string SourceGroupLabel => SourceBadge;
    /// <summary>
    /// Gets the stable source group order used by Discover.
    /// </summary>
    public int SourceGroupSortOrder => ArtifactSource switch
    {
        RegistryArtifactSource.Official => 0,
        RegistryArtifactSource.Community => 1,
        _ => 2
    };
    /// <summary>
    /// Gets the build trust label for the current registry artifact.
    /// </summary>
    public string TrustBadge => GetTrustBadge(_artifactTrust);
    /// <summary>
    /// Gets the explanation for the current registry artifact trust label.
    /// </summary>
    public string TrustTooltip => GetTrustTooltip(_artifactTrust);
    /// <summary>
    /// Gets the persisted source label for an installed copy of this plugin.
    /// </summary>
    public string InstalledSourceBadge => InstallState == PluginInstallState.Bundled
        ? Loc.Instance["Plugins.SourceBundled"]
        : GetSourceBadge(_installedArtifactTrust, installed: true);
    /// <summary>
    /// Gets the persisted source classification for an installed copy of this plugin.
    /// </summary>
    public RegistryArtifactSource InstalledArtifactSource => InstallState == PluginInstallState.Bundled
        ? RegistryArtifactSource.Official
        : _installedArtifactTrust.Source;
    /// <summary>
    /// Gets the persisted build trust label for an installed copy of this plugin.
    /// </summary>
    public string InstalledTrustBadge => InstallState == PluginInstallState.Bundled
        ? Loc.Instance["Plugins.TrustVerifiedOfficial"]
        : GetTrustBadge(_installedArtifactTrust);
    /// <summary>
    /// Gets the explanation for the persisted installed artifact trust label.
    /// </summary>
    public string InstalledTrustTooltip => InstallState == PluginInstallState.Bundled
        ? Loc.Instance["Plugins.TrustTooltipBundled"]
        : GetTrustTooltip(_installedArtifactTrust);

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
    /// Gets whether the plugin needs repair.
    /// </summary>
    public bool NeedsRepair => InstallState == PluginInstallState.Broken;
    /// <summary>
    /// Gets whether repair requires confirmation to adopt the registry source.
    /// </summary>
    public bool RepairRequiresSourceAdoption => NeedsRepair && !_diagnosis.IsRegistryManaged;
    /// <summary>
    /// Gets the localized primary diagnostic message.
    /// </summary>
    public string DiagnosticMessage => _diagnosis.DiagnosticCode switch
    {
        PluginDiagnosticCode.MissingFiles => Loc.Instance["Plugins.DiagnosticMissingFiles"],
        PluginDiagnosticCode.InvalidManifest => Loc.Instance["Plugins.DiagnosticInvalidManifest"],
        PluginDiagnosticCode.IntegrityMismatch => Loc.Instance["Plugins.DiagnosticIntegrityMismatch"],
        PluginDiagnosticCode.LoadFailure => Loc.Instance["Plugins.DiagnosticLoadFailure"],
        PluginDiagnosticCode.PermissionDenied => Loc.Instance["Plugins.DiagnosticPermissionDenied"],
        PluginDiagnosticCode.InterruptedInstallation => Loc.Instance["Plugins.DiagnosticInterruptedInstallation"],
        _ => ""
    };

    /// <summary>
    /// Initializes a new instance of the RegistryPluginItemViewModel class.
    /// </summary>
    public RegistryPluginItemViewModel(RegistryPlugin registryPlugin, PluginRegistryService registryService)
    {
        _registryPlugin = registryPlugin;
        _registryService = registryService;
        _diagnosis = registryService.GetInstallDiagnosis(registryPlugin);
        _artifactTrust = registryService.GetArtifactTrustStatus(registryPlugin);
        _installedArtifactTrust = registryService.GetInstalledArtifactTrustStatus(registryPlugin);
        _installState = _diagnosis.State;
    }

    internal void RefreshInstallState()
    {
        _artifactTrust = _registryService.GetArtifactTrustStatus(_registryPlugin);
        _installedArtifactTrust = _registryService.GetInstalledArtifactTrustStatus(_registryPlugin);
        ApplyDiagnosis(_registryService.GetInstallDiagnosis(_registryPlugin));
        NotifyTrustStateChanged();
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
            RefreshInstallState();
            if (result == PluginInstallResult.PendingRestart)
                InstallState = PluginInstallState.PendingRestart;
            Progress = 1;
        }
        catch (Exception ex)
        {
            RefreshInstallState();
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
        InstallErrorMessage = "";

        try
        {
            var result = await _registryService.UninstallPluginAsync(_registryPlugin.Id);
            RefreshInstallState();
            if (result == PluginUninstallResult.PendingRestart)
                InstallState = PluginInstallState.PendingRestart;
        }
        catch (Exception ex)
        {
            RefreshInstallState();
            InstallErrorMessage = Loc.Instance.GetString("Plugins.UninstallFailedFormat", ex.Message);
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
            RefreshInstallState();
            if (result == PluginInstallResult.PendingRestart)
                InstallState = PluginInstallState.PendingRestart;
            Progress = 1;
        }
        catch (Exception ex)
        {
            RefreshInstallState();
            InstallErrorMessage = Loc.Instance.GetString("Plugins.UpdateFailedFormat", ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        if (IsWorking)
            return;

        var allowSourceAdoption = !RepairRequiresSourceAdoption;
        if (!allowSourceAdoption)
        {
            allowSourceAdoption = MessageBox.Show(
                Loc.Instance.GetString("Plugins.AdoptRegistryConfirm", Name),
                Loc.Instance["Plugins.AdoptRegistryTitle"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        if (!allowSourceAdoption)
            return;

        IsWorking = true;
        Progress = 0;
        InstallErrorMessage = "";

        try
        {
            var progressReporter = new Progress<double>(p => Progress = p);
            var result = await _registryService.RepairPluginAsync(
                _registryPlugin,
                allowSourceAdoption,
                progressReporter);
            RefreshInstallState();
            if (result == PluginInstallResult.PendingRestart)
                InstallState = PluginInstallState.PendingRestart;
            Progress = 1;
        }
        catch (Exception ex)
        {
            RefreshInstallState();
            InstallErrorMessage = Loc.Instance.GetString("Plugins.RepairFailedFormat", ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanRepair() => NeedsRepair && !IsWorking;

    private void ApplyDiagnosis(PluginInstallDiagnosis diagnosis)
    {
        _diagnosis = diagnosis;
        InstallState = diagnosis.State;
        OnPropertyChanged(nameof(NeedsRepair));
        OnPropertyChanged(nameof(RepairRequiresSourceAdoption));
        OnPropertyChanged(nameof(DiagnosticMessage));
        RepairCommand.NotifyCanExecuteChanged();
    }

    private void NotifyTrustStateChanged()
    {
        OnPropertyChanged(nameof(SourceBadge));
        OnPropertyChanged(nameof(ArtifactSource));
        OnPropertyChanged(nameof(SourceGroupLabel));
        OnPropertyChanged(nameof(SourceGroupSortOrder));
        OnPropertyChanged(nameof(TrustBadge));
        OnPropertyChanged(nameof(TrustTooltip));
        OnPropertyChanged(nameof(InstalledSourceBadge));
        OnPropertyChanged(nameof(InstalledArtifactSource));
        OnPropertyChanged(nameof(InstalledTrustBadge));
        OnPropertyChanged(nameof(InstalledTrustTooltip));
    }

    private static string GetSourceBadge(RegistryArtifactValidationResult validation, bool installed) =>
        validation.Source switch
        {
            RegistryArtifactSource.Official => Loc.Instance["Plugins.SourceOfficial"],
            RegistryArtifactSource.Community => Loc.Instance["Plugins.SourceCommunity"],
            _ when installed => Loc.Instance["Plugins.SourceLegacy"],
            _ => Loc.Instance["Plugins.SourceUnknown"]
        };

    private static string GetTrustBadge(RegistryArtifactValidationResult validation) => validation switch
    {
        { IsVerified: true, Source: RegistryArtifactSource.Community } =>
            Loc.Instance["Plugins.TrustVerifiedCommunity"],
        { IsVerified: true } => Loc.Instance["Plugins.TrustVerifiedOfficial"],
        { Code: RegistryArtifactValidationCode.MissingMetadata } => Loc.Instance["Plugins.TrustUnverified"],
        _ => Loc.Instance["Plugins.TrustVerificationFailed"]
    };

    private static string GetTrustTooltip(RegistryArtifactValidationResult validation) => validation switch
    {
        { IsVerified: true, Source: RegistryArtifactSource.Community } =>
            Loc.Instance["Plugins.TrustTooltipVerifiedCommunity"],
        { IsVerified: true } => Loc.Instance["Plugins.TrustTooltipVerifiedOfficial"],
        { Code: RegistryArtifactValidationCode.MissingMetadata } =>
            Loc.Instance["Plugins.TrustTooltipUnverified"],
        _ => Loc.Instance.GetString("Plugins.TrustTooltipFailedFormat", validation.Code)
    };

    partial void OnInstallErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasInstallError));
    }

    partial void OnInstallStateChanged(PluginInstallState value)
    {
        OnPropertyChanged(nameof(IsInstalledOrUpdateAvailable));
        OnPropertyChanged(nameof(NeedsRepair));
        RepairCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsWorkingChanged(bool value)
    {
        RepairCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(DiagnosticMessage));
        NotifyTrustStateChanged();
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
