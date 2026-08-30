using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models.Backup;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Coordinates the settings backup and restore dialogs while keeping all backup semantics in Core.
/// </summary>
public sealed partial class BackupRestoreViewModel : ObservableObject
{
    private static readonly BackupCategory[] OrderedCategories =
    [
        BackupCategory.Workflows,
        BackupCategory.Dictionary,
        BackupCategory.Snippets,
        BackupCategory.Hotkeys,
        BackupCategory.Plugins,
        BackupCategory.History,
        BackupCategory.Preferences
    ];

    private readonly IBackupRestoreService _backupRestoreService;
    private string? _importJson;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPreviewVisible;
    [ObservableProperty] private bool _isResultVisible;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _sourceSummary = "";
    [ObservableProperty] private string _sourceDetails = "";
    [ObservableProperty] private bool _restartRequired;

    /// <summary>Initializes the backup and restore dialog state.</summary>
    public BackupRestoreViewModel(IBackupRestoreService backupRestoreService)
    {
        _backupRestoreService = backupRestoreService;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Gets selectable backup categories in stable display order.</summary>
    public ObservableCollection<BackupCategoryItemViewModel> Categories { get; } = [];

    /// <summary>Gets per-category results from the last restore.</summary>
    public ObservableCollection<BackupCategoryResultViewModel> Results { get; } = [];

    /// <summary>Gets warnings returned by validation or restore.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>Gets whether at least one available category is selected.</summary>
    public bool HasSelectedCategories => Categories.Any(item => item.IsAvailable && item.IsSelected);

    /// <summary>Gets whether an error is currently shown.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>Gets whether warnings are currently shown.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Gets whether the dialog can start its primary operation.</summary>
    public bool CanProceed => !IsBusy && HasSelectedCategories;

    /// <summary>Loads current category counts for the export dialog.</summary>
    public async Task PrepareExportAsync(CancellationToken cancellationToken = default)
    {
        Reset();
        IsBusy = true;
        StatusText = Loc.Instance["Backup.Preparing"];

        try
        {
            var json = await _backupRestoreService.ExportAsync(
                new BackupExportOptions { Categories = BackupCategory.All },
                cancellationToken);
            var preview = _backupRestoreService.PreviewImport(json);
            if (!preview.IsValid)
                throw new InvalidDataException(preview.Error ?? Loc.Instance["Backup.InvalidFile"]);

            SetCategories(preview.Counts, selectAvailable: true, allowEmpty: true);
            ReplaceWarnings(preview.Warnings);
            IsPreviewVisible = true;
            StatusText = "";
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            ErrorMessage = ex.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes a backup containing the selected categories atomically.</summary>
    public async Task ExportToFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var selected = GetSelectedCategories();
        if (selected == 0)
            throw new InvalidOperationException(Loc.Instance["Backup.NoCategories"]);

        IsBusy = true;
        ErrorMessage = "";
        StatusText = Loc.Instance["Backup.Exporting"];
        OnPropertyChanged(nameof(CanProceed));

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(Loc.Instance["Backup.InvalidDestination"]);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = await _backupRestoreService.ExportAsync(
                new BackupExportOptions { Categories = selected },
                cancellationToken);
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
            StatusText = Loc.Instance["Backup.ExportComplete"];
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            ErrorMessage = ex.Message;
            StatusText = "";
            throw;
        }
        finally
        {
            TryDelete(temporaryPath);
            IsBusy = false;
            OnPropertyChanged(nameof(CanProceed));
        }
    }

    /// <summary>Loads and validates an import file before any data is changed.</summary>
    public async Task<bool> PrepareImportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Reset();
        IsBusy = true;
        StatusText = Loc.Instance["Backup.Validating"];

        try
        {
            _importJson = await File.ReadAllTextAsync(path, cancellationToken);
            var preview = _backupRestoreService.PreviewImport(_importJson);
            if (!preview.IsValid)
            {
                ErrorMessage = preview.Error ?? Loc.Instance["Backup.InvalidFile"];
                _importJson = null;
                return false;
            }

            SetSource(preview);
            SetCategories(preview.Counts, selectAvailable: true, allowEmpty: false);
            ReplaceWarnings(preview.Warnings);
            IsPreviewVisible = true;
            StatusText = "";
            return true;
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            ErrorMessage = ex.Message;
            _importJson = null;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Restores the selected categories from the validated import file.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_importJson))
            throw new InvalidOperationException(Loc.Instance["Backup.NoImportLoaded"]);

        var selected = GetSelectedCategories();
        if (selected == 0)
            throw new InvalidOperationException(Loc.Instance["Backup.NoCategories"]);

        IsBusy = true;
        ErrorMessage = "";
        StatusText = Loc.Instance["Backup.Restoring"];
        OnPropertyChanged(nameof(CanProceed));

        try
        {
            var result = await _backupRestoreService.ImportAsync(
                _importJson,
                new BackupImportOptions { Categories = selected },
                cancellationToken);

            Results.Clear();
            foreach (var category in OrderedCategories)
            {
                if (!result.Categories.TryGetValue(category, out var categoryResult))
                    continue;

                Results.Add(new BackupCategoryResultViewModel(category, categoryResult));
            }

            ReplaceWarnings(result.Warnings);
            RestartRequired = result.RestartRequired;
            IsResultVisible = true;
            IsPreviewVisible = false;
            StatusText = result.Success
                ? Loc.Instance["Backup.RestoreComplete"]
                : result.Error ?? Loc.Instance["Backup.RestoreIncomplete"];
            ErrorMessage = result.Success ? "" : result.Error ?? Loc.Instance["Backup.RestoreIncomplete"];
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            ErrorMessage = ex.Message;
            StatusText = "";
            throw;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanProceed));
        }
    }

    /// <summary>Selects all categories that are available in the current operation.</summary>
    public void SelectAll()
    {
        foreach (var item in Categories.Where(item => item.IsAvailable))
            item.IsSelected = true;
    }

    /// <summary>Clears the current category selection.</summary>
    public void SelectNone()
    {
        foreach (var item in Categories)
            item.IsSelected = false;
    }

    private void Reset()
    {
        foreach (var item in Categories)
            item.PropertyChanged -= OnCategoryPropertyChanged;

        Categories.Clear();
        Results.Clear();
        Warnings.Clear();
        _importJson = null;
        IsPreviewVisible = false;
        IsResultVisible = false;
        IsBusy = false;
        StatusText = "";
        ErrorMessage = "";
        SourceSummary = "";
        SourceDetails = "";
        RestartRequired = false;
        RaiseComputedProperties();
    }

    private void SetCategories(
        IReadOnlyDictionary<BackupCategory, int> counts,
        bool selectAvailable,
        bool allowEmpty)
    {
        foreach (var item in Categories)
            item.PropertyChanged -= OnCategoryPropertyChanged;
        Categories.Clear();

        foreach (var category in OrderedCategories)
        {
            counts.TryGetValue(category, out var count);
            var available = allowEmpty || count > 0;
            var item = new BackupCategoryItemViewModel(category, count, available)
            {
                IsSelected = selectAvailable && available
            };
            item.PropertyChanged += OnCategoryPropertyChanged;
            Categories.Add(item);
        }

        RaiseComputedProperties();
    }

    private void SetSource(BackupImportPreview preview)
    {
        SourceSummary = Loc.Instance.GetString(
            "Backup.SourceSummaryFormat",
            string.IsNullOrWhiteSpace(preview.SourcePlatform) ? Loc.Instance["Backup.Unknown"] : preview.SourcePlatform,
            string.IsNullOrWhiteSpace(preview.AppVersion) ? Loc.Instance["Backup.Unknown"] : preview.AppVersion);

        SourceDetails = preview.ExportedAt is { } exportedAt
            ? Loc.Instance.GetString(
                "Backup.ExportedAtFormat",
                exportedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : Loc.Instance.GetString("Backup.ExportedAtFormat", Loc.Instance["Backup.Unknown"]);
    }

    private BackupCategory GetSelectedCategories()
    {
        var categories = (BackupCategory)0;
        foreach (var item in Categories.Where(item => item.IsAvailable && item.IsSelected))
            categories |= item.Category;
        return categories;
    }

    private void ReplaceWarnings(IEnumerable<string> warnings)
    {
        Warnings.Clear();
        foreach (var warning in warnings.Where(value => !string.IsNullOrWhiteSpace(value)))
            Warnings.Add(warning);
        OnPropertyChanged(nameof(HasWarnings));
    }

    private void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupCategoryItemViewModel.IsSelected))
            RaiseComputedProperties();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in Categories)
            item.RefreshLocalization();
        foreach (var result in Results)
            result.RefreshLocalization();

        OnPropertyChanged(nameof(StatusText));
    }

    private void RaiseComputedProperties()
    {
        OnPropertyChanged(nameof(HasSelectedCategories));
        OnPropertyChanged(nameof(CanProceed));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanProceed));

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup after an interrupted export.
        }
    }
}

/// <summary>Represents one selectable backup category.</summary>
public sealed partial class BackupCategoryItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    /// <summary>Initializes a category row.</summary>
    public BackupCategoryItemViewModel(BackupCategory category, int count, bool isAvailable)
    {
        Category = category;
        Count = Math.Max(0, count);
        IsAvailable = isAvailable;
    }

    /// <summary>Gets the underlying backup category.</summary>
    public BackupCategory Category { get; }

    /// <summary>Gets the number of entries in the category.</summary>
    public int Count { get; }

    /// <summary>Gets whether the category can be selected.</summary>
    public bool IsAvailable { get; }

    /// <summary>Gets the localized category name.</summary>
    public string DisplayName => Loc.Instance[$"Backup.Category.{Category}"];

    /// <summary>Gets the localized entry count.</summary>
    public string CountDisplay => Loc.Instance.GetString("Backup.CategoryCountFormat", Count);

    /// <summary>Gets whether this category contains transcription history.</summary>
    public bool IsHistory => Category == BackupCategory.History;

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CountDisplay));
    }
}

/// <summary>Represents one category in the restore result summary.</summary>
public sealed class BackupCategoryResultViewModel : ObservableObject
{
    private readonly BackupCategoryImportResult _result;

    /// <summary>Initializes a restore result row.</summary>
    public BackupCategoryResultViewModel(BackupCategory category, BackupCategoryImportResult result)
    {
        Category = category;
        _result = result;
    }

    /// <summary>Gets the underlying backup category.</summary>
    public BackupCategory Category { get; }

    /// <summary>Gets the localized category name.</summary>
    public string DisplayName => Loc.Instance[$"Backup.Category.{Category}"];

    /// <summary>Gets the localized result detail.</summary>
    public string Detail => Loc.Instance.GetString(
        "Backup.ResultDetailFormat",
        _result.Imported,
        _result.Skipped,
        _result.Conflicts);

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Detail));
    }
}
