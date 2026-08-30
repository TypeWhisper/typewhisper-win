using System.IO;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models.Backup;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class BackupRestoreViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"typewhisper-backup-ui-{Guid.NewGuid():N}");

    [Fact]
    public async Task PrepareExport_PopulatesAllCategoriesAndCurrentCounts()
    {
        var service = new FakeBackupRestoreService
        {
            Preview = ValidPreview(new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Workflows] = 4,
                [BackupCategory.History] = 12
            })
        };
        var viewModel = new BackupRestoreViewModel(service);

        await viewModel.PrepareExportAsync();

        Assert.Equal(7, viewModel.Categories.Count);
        Assert.Equal(4, viewModel.Categories.Single(item => item.Category == BackupCategory.Workflows).Count);
        Assert.All(viewModel.Categories, item => Assert.True(item.IsSelected));
        Assert.True(viewModel.CanProceed);
    }

    [Fact]
    public async Task ExportToFile_UsesSelectedCategoriesAndReplacesDestination()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "backup.json");
        await File.WriteAllTextAsync(destination, "old");
        var service = new FakeBackupRestoreService
        {
            ExportJson = "{\"format\":\"typewhisper-backup\"}",
            Preview = ValidPreview(new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Workflows] = 1,
                [BackupCategory.History] = 1
            })
        };
        var viewModel = new BackupRestoreViewModel(service);
        await viewModel.PrepareExportAsync();
        viewModel.Categories.Single(item => item.Category == BackupCategory.History).IsSelected = false;

        await viewModel.ExportToFileAsync(destination);

        Assert.Equal("{\"format\":\"typewhisper-backup\"}", await File.ReadAllTextAsync(destination));
        Assert.Equal(
            BackupCategory.All & ~BackupCategory.History,
            service.LastExportOptions?.Categories);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task PrepareImport_RejectsInvalidBackupBeforeRestore()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "invalid.json");
        await File.WriteAllTextAsync(source, "not a backup");
        var service = new FakeBackupRestoreService
        {
            Preview = new BackupImportPreview { IsValid = false, Error = "Unsupported schema" }
        };
        var viewModel = new BackupRestoreViewModel(service);

        var loaded = await viewModel.PrepareImportAsync(source);

        Assert.False(loaded);
        Assert.Equal("Unsupported schema", viewModel.ErrorMessage);
        Assert.False(viewModel.IsPreviewVisible);
    }

    [Fact]
    public async Task Restore_UsesSelectionAndBuildsDetailedResult()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "backup.json");
        await File.WriteAllTextAsync(source, "{}");
        var service = new FakeBackupRestoreService
        {
            Preview = ValidPreview(new Dictionary<BackupCategory, int>
            {
                [BackupCategory.Dictionary] = 3,
                [BackupCategory.Snippets] = 2
            }),
            ImportResult = new BackupImportResult
            {
                Success = true,
                RestartRequired = true,
                Categories = new Dictionary<BackupCategory, BackupCategoryImportResult>
                {
                    [BackupCategory.Dictionary] = new() { Imported = 2, Skipped = 1 },
                    [BackupCategory.Snippets] = new() { Imported = 1, Conflicts = 1 }
                },
                Warnings = ["Plugin unavailable"]
            }
        };
        var viewModel = new BackupRestoreViewModel(service);
        Assert.True(await viewModel.PrepareImportAsync(source));
        viewModel.Categories.Single(item => item.Category == BackupCategory.Snippets).IsSelected = false;

        await viewModel.RestoreAsync();

        Assert.Equal(BackupCategory.Dictionary, service.LastImportOptions?.Categories);
        Assert.True(viewModel.IsResultVisible);
        Assert.False(viewModel.IsPreviewVisible);
        Assert.True(viewModel.RestartRequired);
        Assert.Equal(2, viewModel.Results.Count);
        Assert.Single(viewModel.Warnings);
    }

    [Fact]
    public async Task StaleExportPreparation_DoesNotOverwriteLaterImportSelection()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "backup.json");
        await File.WriteAllTextAsync(source, "import");
        var exportCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeBackupRestoreService
        {
            ExportCompletion = exportCompletion,
            PreviewFactory = json => ValidPreview(new Dictionary<BackupCategory, int>
            {
                [json == "import" ? BackupCategory.Dictionary : BackupCategory.History] = 1
            })
        };
        var viewModel = new BackupRestoreViewModel(service);

        var staleExport = viewModel.PrepareExportAsync();
        Assert.True(await viewModel.PrepareImportAsync(source));
        viewModel.Categories.Single(item => item.Category == BackupCategory.Dictionary).IsSelected = false;

        exportCompletion.SetResult("export");
        await staleExport;

        Assert.False(viewModel.Categories.Single(item => item.Category == BackupCategory.Dictionary).IsSelected);
        Assert.True(viewModel.IsPreviewVisible);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static BackupImportPreview ValidPreview(IReadOnlyDictionary<BackupCategory, int> counts) =>
        new()
        {
            IsValid = true,
            SourcePlatform = "windows",
            AppVersion = "1.0.0",
            ExportedAt = DateTimeOffset.UtcNow,
            Counts = counts
        };

    private sealed class FakeBackupRestoreService : IBackupRestoreService
    {
        public string ExportJson { get; init; } = "{}";
        public BackupImportPreview Preview { get; init; } = ValidPreview(
            new Dictionary<BackupCategory, int>());
        public BackupImportResult ImportResult { get; init; } = new() { Success = true };
        public TaskCompletionSource<string>? ExportCompletion { get; init; }
        public Func<string, BackupImportPreview>? PreviewFactory { get; init; }
        public BackupExportOptions? LastExportOptions { get; private set; }
        public BackupImportOptions? LastImportOptions { get; private set; }

        public Task<string> ExportAsync(
            BackupExportOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastExportOptions = options;
            return ExportCompletion?.Task ?? Task.FromResult(ExportJson);
        }

        public BackupImportPreview PreviewImport(string json) => PreviewFactory?.Invoke(json) ?? Preview;

        public Task<BackupImportResult> ImportAsync(
            string json,
            BackupImportOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastImportOptions = options;
            return Task.FromResult(ImportResult);
        }
    }
}
