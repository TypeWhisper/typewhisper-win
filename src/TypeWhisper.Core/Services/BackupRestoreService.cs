using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Models.Backup;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Creates versioned portable backups and merges them into the current profile.
/// </summary>
public sealed class BackupRestoreService : IBackupRestoreService
{
    internal const int MaximumBackupBytes = 64 * 1024 * 1024;
    internal const int MaximumWorkflows = 10_000;
    internal const int MaximumDictionaryEntries = 50_000;
    internal const int MaximumSnippets = 50_000;
    internal const int MaximumPlugins = 10_000;
    internal const int MaximumHistoryEntries = 100_000;

    private const int MaximumShortTextLength = 16_384;
    private const int MaximumTranscriptionTextLength = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions)
    {
        WriteIndented = false
    };

    private static readonly string[] HotkeyActions =
    [
        "mainDictation",
        "toggleOnly",
        "holdOnly",
        "recentTranscriptions",
        "copyLastTranscription",
        "workflowPalette",
        "recorderToggle"
    ];

    private readonly ISettingsService _settingsService;
    private readonly IWorkflowService _workflowService;
    private readonly IDictionaryService _dictionaryService;
    private readonly ISnippetService _snippetService;
    private readonly IHistoryService _historyService;
    private readonly IBackupPluginHandler? _pluginHandler;
    private readonly IUsageStatisticsService? _usageStatisticsService;
    private readonly IBackupTermPackHandler? _termPackHandler;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    /// <summary>
    /// Initializes a backup service over the profile's existing data services.
    /// </summary>
    public BackupRestoreService(
        ISettingsService settingsService,
        IWorkflowService workflowService,
        IDictionaryService dictionaryService,
        ISnippetService snippetService,
        IHistoryService historyService,
        IBackupPluginHandler? pluginHandler = null,
        IUsageStatisticsService? usageStatisticsService = null,
        IBackupTermPackHandler? termPackHandler = null)
    {
        _settingsService = settingsService;
        _workflowService = workflowService;
        _dictionaryService = dictionaryService;
        _snippetService = snippetService;
        _historyService = historyService;
        _pluginHandler = pluginHandler;
        _usageStatisticsService = usageStatisticsService;
        _termPackHandler = termPackHandler;
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(
        BackupExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BackupExportOptions();
        cancellationToken.ThrowIfCancellationRequested();
        await _historyService.EnsureLoadedAsync().ConfigureAwait(false);

        var settings = _settingsService.Current;
        var selected = options.Categories & BackupCategory.All;
        var workflows = selected.HasFlag(BackupCategory.Workflows)
            ? _workflowService.Workflows.Select(ToBackupWorkflow).ToList()
            : [];
        var workflowNames = _workflowService.Workflows.ToDictionary(
            workflow => workflow.Id,
            workflow => workflow.Name,
            StringComparer.Ordinal);

        IReadOnlyList<BackupPlugin> plugins = [];
        if (selected.HasFlag(BackupCategory.Plugins) && _pluginHandler is not null)
            plugins = await _pluginHandler.ExportAsync(cancellationToken).ConfigureAwait(false);

        var document = new SettingsBackupDocument
        {
            AppVersion = ResolveAppVersion(),
            Data = new SettingsBackupData
            {
                Workflows = workflows,
                Dictionary = selected.HasFlag(BackupCategory.Dictionary)
                    ? new BackupDictionary
                    {
                        Entries = _dictionaryService.Entries
                            .Where(IsUserAuthoredDictionaryEntry)
                            .Select(ToBackupDictionaryEntry)
                            .ToList(),
                        EnabledPackIds = settings.EnabledPackIds
                            .Where(static id => !string.IsNullOrWhiteSpace(id))
                            .Select(static id => id.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    }
                    : new BackupDictionary(),
                Snippets = selected.HasFlag(BackupCategory.Snippets)
                    ? _snippetService.Snippets.Select(ToBackupSnippet).ToList()
                    : [],
                Hotkeys = selected.HasFlag(BackupCategory.Hotkeys)
                    ? ToBackupHotkeys(settings)
                    : new BackupHotkeys(),
                Plugins = plugins,
                History = selected.HasFlag(BackupCategory.History)
                    ? _historyService.Records.Select(record => ToBackupHistoryEntry(record, workflowNames)).ToList()
                    : [],
                Preferences = selected.HasFlag(BackupCategory.Preferences)
                    ? ToBackupPreferences(settings)
                    : null
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <inheritdoc />
    public BackupImportPreview PreviewImport(string json)
    {
        if (!TryReadAndValidate(json, out var document, out var error, out var warnings))
            return new BackupImportPreview { Error = error, Warnings = warnings };

        return new BackupImportPreview
        {
            IsValid = true,
            SourcePlatform = document!.SourcePlatform,
            ExportedAt = document.ExportedAt,
            AppVersion = document.AppVersion,
            Counts = CountCategories(document.Data),
            Warnings = warnings
        };
    }

    /// <inheritdoc />
    public async Task<BackupImportResult> ImportAsync(
        string json,
        BackupImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BackupImportOptions();
        if (!TryReadAndValidate(json, out var document, out var error, out var validationWarnings))
            return new BackupImportResult { Error = error, Warnings = validationWarnings };

        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _historyService.EnsureLoadedAsync().ConfigureAwait(false);
            return await ImportValidatedAsync(
                document!,
                options.Categories & BackupCategory.All,
                validationWarnings,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackupImportResult
            {
                Error = $"The backup could not be restored: {ex.Message}",
                Warnings = validationWarnings
            };
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<BackupImportResult> ImportValidatedAsync(
        SettingsBackupDocument document,
        BackupCategory selected,
        IReadOnlyList<string> validationWarnings,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<BackupCategory, BackupCategoryImportResult>();
        var warnings = validationWarnings.ToList();
        using (ProfileMutationCoordinator.Enter())
        {
            var settingsSnapshot = _settingsService.Current;
            var workflowSnapshot = _workflowService.Workflows.ToList();
            var dictionarySnapshot = _dictionaryService.Entries.ToList();
            var snippetSnapshot = _snippetService.Snippets.ToList();
            var historySnapshot = _historyService.Records.ToList();

            var workflowCandidate = workflowSnapshot;
            var dictionaryCandidate = dictionarySnapshot;
            var snippetCandidate = snippetSnapshot;
            var historyCandidate = historySnapshot;
            var settingsCandidate = settingsSnapshot;
            IReadOnlyList<TranscriptionRecord> importedHistoryRecords = [];

            if (selected.HasFlag(BackupCategory.Workflows))
                (workflowCandidate, results[BackupCategory.Workflows]) = MergeWorkflows(workflowSnapshot, document.Data.Workflows);

            if (selected.HasFlag(BackupCategory.Dictionary))
            {
                (dictionaryCandidate, var dictionaryResult) = MergeDictionary(dictionarySnapshot, document.Data.Dictionary.Entries);
                var mergedPackIds = settingsSnapshot.EnabledPackIds
                    .Concat(document.Data.Dictionary.EnabledPackIds)
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .Select(static id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var newPackCount = mergedPackIds.Length - settingsSnapshot.EnabledPackIds
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                settingsCandidate = settingsCandidate with { EnabledPackIds = mergedPackIds };
                results[BackupCategory.Dictionary] = dictionaryResult with
                {
                    Imported = dictionaryResult.Imported + Math.Max(0, newPackCount),
                    Skipped = dictionaryResult.Skipped + document.Data.Dictionary.EnabledPackIds.Count - Math.Max(0, newPackCount)
                };
            }

            if (selected.HasFlag(BackupCategory.Snippets))
                (snippetCandidate, results[BackupCategory.Snippets]) = MergeSnippets(snippetSnapshot, document.Data.Snippets);

            if (selected.HasFlag(BackupCategory.Hotkeys))
                (settingsCandidate, results[BackupCategory.Hotkeys]) = MergeHotkeys(settingsCandidate, document.Data.Hotkeys);

            if (selected.HasFlag(BackupCategory.History))
                (historyCandidate, results[BackupCategory.History], importedHistoryRecords) = MergeHistory(
                    historySnapshot,
                    document.Data.History,
                    workflowCandidate,
                    settingsSnapshot);

            if (selected.HasFlag(BackupCategory.Preferences) && document.Data.Preferences is not null)
            {
                if (PreferencesEqual(settingsCandidate, document.Data.Preferences))
                {
                    results[BackupCategory.Preferences] = new BackupCategoryImportResult { Skipped = 1 };
                }
                else
                {
                    settingsCandidate = ApplyPreferences(settingsCandidate, document.Data.Preferences);
                    results[BackupCategory.Preferences] = new BackupCategoryImportResult { Imported = 1 };
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var workflowsChanged = results.GetValueOrDefault(BackupCategory.Workflows)?.Imported > 0;
            var dictionaryChanged = results.GetValueOrDefault(BackupCategory.Dictionary)?.Imported > 0;
            var snippetsChanged = results.GetValueOrDefault(BackupCategory.Snippets)?.Imported > 0;
            var historyChanged = results.GetValueOrDefault(BackupCategory.History)?.Imported > 0;
            var settingsChanged = !Equals(settingsSnapshot, settingsCandidate);

            try
            {
                if (workflowsChanged && !_workflowService.TryReplaceAll(workflowCandidate))
                    throw new IOException("Workflows could not be persisted.");
                if (dictionaryChanged && !_dictionaryService.TryReplaceAll(dictionaryCandidate))
                    throw new IOException("Dictionary entries could not be persisted.");
                if (snippetsChanged && !_snippetService.TryReplaceAll(snippetCandidate))
                    throw new IOException("Snippets could not be persisted.");
                if (historyChanged && !_historyService.TryReplaceAll(historyCandidate))
                    throw new IOException("History entries could not be persisted.");
                if (settingsChanged)
                    _settingsService.Save(settingsCandidate);

                if (selected.HasFlag(BackupCategory.Dictionary))
                {
                    foreach (var packId in settingsCandidate.EnabledPackIds)
                    {
                        if (TermPack.FindById(packId) is { } pack)
                            _dictionaryService.ActivatePack(pack);
                    }
                }
            }
            catch (Exception ex)
            {
                var rollbackFailures = RollBack(
                    settingsSnapshot,
                    workflowSnapshot,
                    dictionarySnapshot,
                    snippetSnapshot,
                    historySnapshot,
                    workflowsChanged,
                    dictionaryChanged,
                    snippetsChanged,
                    historyChanged,
                    settingsChanged);
                if (rollbackFailures.Count > 0)
                {
                    warnings.Add(
                        $"Rollback could not restore: {string.Join(", ", rollbackFailures)}.");
                }
                return new BackupImportResult
                {
                    Error = rollbackFailures.Count == 0
                        ? $"The local restore was rolled back: {ex.Message}"
                        : $"The local restore failed and rollback was incomplete: {ex.Message}",
                    Warnings = warnings,
                    Categories = results
                };
            }

            if (_usageStatisticsService is not null)
            {
                try
                {
                    foreach (var record in importedHistoryRecords.Where(record => record.Status == TranscriptionRecordStatus.Succeeded))
                    {
                        _usageStatisticsService.RecordTranscription(
                            record.Timestamp,
                            record.WordCount,
                            record.DurationSeconds,
                            record.AppProcessName,
                            record.AppName,
                            record.EngineUsed,
                            record.ModelUsed);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"History was restored, but usage statistics could not be updated: {ex.Message}");
                }
            }
        }

        if (selected.HasFlag(BackupCategory.Dictionary) && _termPackHandler is not null)
        {
            try
            {
                var packWarnings = await _termPackHandler.MaterializeAsync(
                    _settingsService.Current.EnabledPackIds,
                    cancellationToken).ConfigureAwait(false);
                warnings.AddRange(packWarnings);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"Dictionary settings were restored, but packs could not be activated: {ex.Message}");
            }
        }

        var restartRequired = false;
        if (selected.HasFlag(BackupCategory.Plugins))
        {
            if (_pluginHandler is null)
            {
                results[BackupCategory.Plugins] = new BackupCategoryImportResult
                {
                    Skipped = document.Data.Plugins.Count
                };
                if (document.Data.Plugins.Count > 0)
                    warnings.Add("Plugin restore is not available in this application context.");
            }
            else
            {
                var pluginResult = await _pluginHandler.ImportAsync(document.Data.Plugins, cancellationToken)
                    .ConfigureAwait(false);
                results[BackupCategory.Plugins] = pluginResult.CategoryResult;
                warnings.AddRange(pluginResult.Warnings);
                restartRequired = pluginResult.RestartRequired;
            }
        }

        return new BackupImportResult
        {
            Success = true,
            RestartRequired = restartRequired,
            Categories = results,
            Warnings = warnings
        };
    }

    private IReadOnlyList<string> RollBack(
        AppSettings settings,
        IReadOnlyList<Workflow> workflows,
        IReadOnlyList<DictionaryEntry> dictionary,
        IReadOnlyList<Snippet> snippets,
        IReadOnlyList<TranscriptionRecord> history,
        bool workflowsChanged,
        bool dictionaryChanged,
        bool snippetsChanged,
        bool historyChanged,
        bool settingsChanged)
    {
        var failures = new List<string>();
        TryRollBack(historyChanged, "history", () => _historyService.TryReplaceAll(history), failures);
        TryRollBack(snippetsChanged, "snippets", () => _snippetService.TryReplaceAll(snippets), failures);
        TryRollBack(dictionaryChanged, "dictionary", () => _dictionaryService.TryReplaceAll(dictionary), failures);
        TryRollBack(workflowsChanged, "workflows", () => _workflowService.TryReplaceAll(workflows), failures);
        if (settingsChanged)
        {
            try
            {
                _settingsService.Save(settings);
            }
            catch (Exception ex)
            {
                failures.Add($"settings ({ex.Message})");
            }
        }

        return failures;
    }

    private static void TryRollBack(
        bool changed,
        string category,
        Func<bool> restore,
        ICollection<string> failures)
    {
        if (!changed)
            return;

        try
        {
            if (!restore())
                failures.Add(category);
        }
        catch (Exception ex)
        {
            failures.Add($"{category} ({ex.Message})");
        }
    }

    private static (List<Workflow>, BackupCategoryImportResult) MergeWorkflows(
        IReadOnlyList<Workflow> current,
        IReadOnlyList<BackupWorkflow> incoming)
    {
        var merged = current.ToList();
        var fingerprints = current.Select(ToBackupWorkflow).Select(Fingerprint).ToHashSet(StringComparer.Ordinal);
        var names = current.Select(workflow => workflow.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextSortOrder = current.Count == 0 ? 0 : current.Max(workflow => workflow.SortOrder) + 1;
        var imported = 0;
        var skipped = 0;
        var conflicts = 0;

        foreach (var item in incoming)
        {
            var fingerprint = Fingerprint(item);
            if (!fingerprints.Add(fingerprint))
            {
                skipped++;
                continue;
            }
            if (!names.Add(item.Name.Trim()))
            {
                conflicts++;
                continue;
            }

            var now = DateTime.UtcNow;
            merged.Add(new Workflow
            {
                Id = Guid.NewGuid().ToString(),
                Name = item.Name.Trim(),
                IsEnabled = item.IsEnabled,
                SortOrder = nextSortOrder++,
                Template = item.Template,
                Trigger = item.Trigger,
                Behavior = item.Behavior,
                Output = item.Output,
                CreatedAt = now,
                UpdatedAt = now
            });
            imported++;
        }

        return (merged, new BackupCategoryImportResult { Imported = imported, Skipped = skipped, Conflicts = conflicts });
    }

    private static (List<DictionaryEntry>, BackupCategoryImportResult) MergeDictionary(
        IReadOnlyList<DictionaryEntry> current,
        IReadOnlyList<BackupDictionaryEntry> incoming)
    {
        var merged = current.ToList();
        var currentByKey = current
            .Where(IsUserAuthoredDictionaryEntry)
            .GroupBy(DictionaryKey)
            .ToDictionary(group => group.Key, group => group.First());
        var imported = 0;
        var skipped = 0;
        var conflicts = 0;

        foreach (var item in incoming)
        {
            var key = DictionaryKey(item);
            if (currentByKey.TryGetValue(key, out var existing))
            {
                if (DictionaryValuesEqual(existing, item)) skipped++; else conflicts++;
                continue;
            }

            var now = DateTime.UtcNow;
            var entry = new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = item.EntryType,
                Original = item.Original.Trim(),
                Replacement = item.EntryType == DictionaryEntryType.Correction ? item.Replacement ?? "" : null,
                CaseSensitive = item.CaseSensitive,
                IsRegex = item.IsRegex,
                IsEnabled = item.IsEnabled,
                Source = item.Source,
                CreatedAt = now,
                UpdatedAt = now
            };
            merged.Add(entry);
            currentByKey[key] = entry;
            imported++;
        }

        return (merged, new BackupCategoryImportResult { Imported = imported, Skipped = skipped, Conflicts = conflicts });
    }

    private static (List<Snippet>, BackupCategoryImportResult) MergeSnippets(
        IReadOnlyList<Snippet> current,
        IReadOnlyList<BackupSnippet> incoming)
    {
        var merged = current.ToList();
        var currentByTrigger = current
            .GroupBy(snippet => snippet.Trigger.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var skipped = 0;
        var conflicts = 0;

        foreach (var item in incoming)
        {
            var trigger = item.Trigger.Trim();
            if (currentByTrigger.TryGetValue(trigger, out var existing))
            {
                if (SnippetValuesEqual(existing, item)) skipped++; else conflicts++;
                continue;
            }

            var now = DateTime.UtcNow;
            var snippet = new Snippet
            {
                Id = Guid.NewGuid().ToString(),
                Trigger = trigger,
                Replacement = item.Replacement,
                CaseSensitive = item.CaseSensitive,
                IsEnabled = item.IsEnabled,
                Tags = item.Tags,
                CreatedAt = now,
                UpdatedAt = now
            };
            merged.Add(snippet);
            currentByTrigger[trigger] = snippet;
            imported++;
        }

        return (merged, new BackupCategoryImportResult { Imported = imported, Skipped = skipped, Conflicts = conflicts });
    }

    private static (AppSettings, BackupCategoryImportResult) MergeHotkeys(
        AppSettings settings,
        BackupHotkeys incoming)
    {
        var bindings = new Dictionary<string, IReadOnlyList<string>>(
            incoming.Bindings,
            StringComparer.OrdinalIgnoreCase);
        var occupied = CurrentHotkeyBindings(settings)
            .SelectMany(pair => pair.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var skipped = 0;
        var conflicts = 0;
        var replacements = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in HotkeyActions)
        {
            var current = CurrentHotkeyBindings(settings)[action];
            if (!bindings.TryGetValue(action, out var candidates) || candidates.Count == 0)
                continue;
            if (current.Count > 0)
            {
                skipped += candidates.Count;
                continue;
            }

            var accepted = new List<string>();
            foreach (var hotkey in candidates.Select(static value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!occupied.Add(hotkey))
                {
                    conflicts++;
                    continue;
                }
                accepted.Add(hotkey);
                imported++;
            }
            replacements[action] = accepted;
        }

        settings = settings with
        {
            MainDictationHotkeys = replacements.GetValueOrDefault("mainDictation", settings.GetMainDictationHotkeys()),
            ToggleOnlyHotkeys = replacements.GetValueOrDefault("toggleOnly", settings.GetToggleOnlyHotkeys()),
            HoldOnlyHotkeys = replacements.GetValueOrDefault("holdOnly", settings.GetHoldOnlyHotkeys()),
            RecentTranscriptionsHotkeys = replacements.GetValueOrDefault("recentTranscriptions", settings.GetRecentTranscriptionsHotkeys()),
            CopyLastTranscriptionHotkeys = replacements.GetValueOrDefault("copyLastTranscription", settings.GetCopyLastTranscriptionHotkeys()),
            WorkflowPaletteHotkeys = replacements.GetValueOrDefault("workflowPalette", settings.GetWorkflowPaletteHotkeys()),
            RecorderToggleHotkeys = replacements.GetValueOrDefault("recorderToggle", settings.GetRecorderToggleHotkeys())
        };
        return (settings.NormalizeHotkeyLists(), new BackupCategoryImportResult
        {
            Imported = imported,
            Skipped = skipped,
            Conflicts = conflicts
        });
    }

    private static (List<TranscriptionRecord>, BackupCategoryImportResult, IReadOnlyList<TranscriptionRecord>) MergeHistory(
        IReadOnlyList<TranscriptionRecord> current,
        IReadOnlyList<BackupHistoryEntry> incoming,
        IReadOnlyList<Workflow> workflows,
        AppSettings destinationSettings)
    {
        var merged = current.ToList();
        var fingerprints = current.Select(HistoryFingerprint).ToHashSet(StringComparer.Ordinal);
        var workflowIds = workflows
            .GroupBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var cutoff = destinationSettings.HistoryRetentionMode == HistoryRetentionMode.Duration
            ? DateTime.UtcNow.AddMinutes(-Math.Max(1, destinationSettings.HistoryRetentionMinutes))
            : (DateTime?)null;
        var imported = 0;
        var skipped = 0;
        var importedRecords = new List<TranscriptionRecord>();

        foreach (var item in incoming)
        {
            var timestamp = NormalizeUtc(item.Timestamp);
            if (cutoff.HasValue && timestamp < cutoff.Value)
            {
                skipped++;
                continue;
            }

            var fingerprint = HistoryFingerprint(item);
            if (!fingerprints.Add(fingerprint))
            {
                skipped++;
                continue;
            }

            var importedRecord = new TranscriptionRecord
            {
                Id = $"backup:{fingerprint}",
                Timestamp = timestamp,
                RawText = item.RawText,
                FinalText = item.FinalText,
                AppName = item.AppName,
                AppProcessName = item.AppProcessName,
                AppUrl = item.AppUrl,
                DurationSeconds = item.DurationSeconds,
                Language = item.Language,
                WorkflowId = item.WorkflowName is not null && workflowIds.TryGetValue(item.WorkflowName, out var workflowId)
                    ? workflowId
                    : null,
                Status = item.Status,
                EngineUsed = item.EngineUsed,
                ModelUsed = item.ModelUsed,
                TranscriptionTaskUsed = item.TranscriptionTaskUsed,
                UsedTranscriptionFallback = item.UsedTranscriptionFallback,
                AudioFileName = null,
                RecoveryAudioFileName = null,
                CreatedAt = timestamp
            };
            merged.Add(importedRecord);
            importedRecords.Add(importedRecord);
            imported++;
        }

        merged = merged.OrderByDescending(record => record.Timestamp).ToList();
        return (merged, new BackupCategoryImportResult { Imported = imported, Skipped = skipped }, importedRecords);
    }

    private static bool TryReadAndValidate(
        string json,
        out SettingsBackupDocument? document,
        out string? error,
        out IReadOnlyList<string> warnings)
    {
        document = null;
        error = null;
        var warningList = new List<string>();
        warnings = warningList;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The selected backup file is empty.";
            return false;
        }
        if (json.Length > MaximumBackupBytes || Encoding.UTF8.GetByteCount(json) > MaximumBackupBytes)
        {
            error = $"The backup exceeds the {MaximumBackupBytes / 1024 / 1024} MB size limit.";
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 64,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            document = JsonSerializer.Deserialize<SettingsBackupDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"The selected file is not a valid TypeWhisper backup: {ex.Message}";
            return false;
        }

        if (document?.Data is null)
        {
            error = "The backup does not contain a data section.";
            return false;
        }
        if (!string.Equals(document.Format, SettingsBackupDocument.CurrentFormat, StringComparison.Ordinal))
        {
            error = "The selected file is not a TypeWhisper backup.";
            return false;
        }
        if (document.SchemaVersion != SettingsBackupDocument.CurrentSchemaVersion)
        {
            error = document.SchemaVersion > SettingsBackupDocument.CurrentSchemaVersion
                ? "This backup was created with a newer unsupported schema version."
                : "This backup schema version is not supported.";
            return false;
        }
        if (document.ExportedAt == default || string.IsNullOrWhiteSpace(document.SourcePlatform))
        {
            error = "The backup metadata is incomplete.";
            return false;
        }
        if (!string.Equals(document.SourcePlatform, "windows", StringComparison.OrdinalIgnoreCase))
            warningList.Add($"This backup was created on {document.SourcePlatform}; some categories may not be portable.");

        try
        {
            error = ValidateData(document.Data);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or ArgumentException)
        {
            error = $"The backup data structure is invalid: {ex.Message}";
        }
        return error is null;
    }

    private static string? ValidateData(SettingsBackupData data)
    {
        if (data.Workflows is null || data.Dictionary is null || data.Dictionary.Entries is null
            || data.Dictionary.EnabledPackIds is null || data.Snippets is null || data.Hotkeys is null
            || data.Hotkeys.Bindings is null || data.Plugins is null || data.History is null)
            return "The backup contains a null collection or category.";
        if (data.Workflows.Any(static workflow => workflow is null)
            || data.Dictionary.Entries.Any(static entry => entry is null)
            || data.Dictionary.EnabledPackIds.Any(static id => id is null)
            || data.Snippets.Any(static snippet => snippet is null)
            || data.Plugins.Any(static plugin => plugin is null)
            || data.History.Any(static entry => entry is null))
            return "The backup contains a null item.";

        if (data.Workflows.Count > MaximumWorkflows) return "The backup contains too many workflows.";
        if (data.Dictionary.Entries.Count > MaximumDictionaryEntries) return "The backup contains too many dictionary entries.";
        if (data.Snippets.Count > MaximumSnippets) return "The backup contains too many snippets.";
        if (data.Plugins.Count > MaximumPlugins) return "The backup contains too many plugins.";
        if (data.History.Count > MaximumHistoryEntries) return "The backup contains too many history entries.";
        if (data.Dictionary.EnabledPackIds.Count > 1_000) return "The backup contains too many dictionary packs.";
        if (data.Hotkeys.Bindings.Count > HotkeyActions.Length) return "The backup contains unknown hotkey actions.";

        foreach (var workflow in data.Workflows)
        {
            if (workflow.Trigger is null || workflow.Behavior is null || workflow.Output is null
                || workflow.Trigger.ProcessNames is null || workflow.Trigger.WebsitePatterns is null
                || workflow.Trigger.Hotkeys is null || workflow.Behavior.Settings is null
                || workflow.Behavior.InputLanguageHints is null)
                return "A workflow contains a null object or collection.";
            if (!ValidRequired(workflow.Name) || workflow.Name.Length > 512)
                return "A workflow has an invalid name.";
            if (!Enum.IsDefined(workflow.Template) || !Enum.IsDefined(workflow.Trigger.Kind))
                return "A workflow contains an unsupported enum value.";
            if (workflow.Trigger.ProcessNames.Count > 1_000 || workflow.Trigger.WebsitePatterns.Count > 1_000 || workflow.Trigger.Hotkeys.Count > 32)
                return "A workflow contains too many trigger values.";
            if (workflow.Trigger.ProcessNames.Any(value => !ValidRequired(value) || !ValidShort(value))
                || workflow.Trigger.WebsitePatterns.Any(value => !ValidRequired(value) || !ValidShort(value))
                || workflow.Trigger.Hotkeys.Any(value => !ValidHotkey(value)))
                return "A workflow contains an invalid trigger value.";
            if (workflow.Behavior.Settings.Count > 1_000
                || workflow.Behavior.Settings.Any(pair => !ValidRequired(pair.Key) || pair.Value is null || !ValidShort(pair.Key) || !ValidShort(pair.Value))
                || workflow.Behavior.FineTuning is null || !ValidShort(workflow.Behavior.FineTuning)
                || !ValidShort(workflow.Behavior.ProviderOverride) || !ValidShort(workflow.Behavior.ModelOverride)
                || !ValidShort(workflow.Behavior.InputLanguage) || !ValidShort(workflow.Behavior.SelectedTask)
                || !ValidShort(workflow.Behavior.TranslationTarget)
                || workflow.Behavior.InputLanguageHints.Any(value => !ValidRequired(value) || !ValidShort(value))
                || !ValidShort(workflow.Output.Format) || !ValidShort(workflow.Output.TargetActionPluginId)
                || !ValidShort(workflow.Output.NumberNormalizationModeRaw))
                return "A workflow contains invalid settings.";
        }
        foreach (var entry in data.Dictionary.Entries)
        {
            if (!ValidRequired(entry.Original) || !ValidShort(entry.Original) || !ValidShort(entry.Replacement))
                return "A dictionary entry contains invalid text.";
            if (!Enum.IsDefined(entry.EntryType) || !Enum.IsDefined(entry.Source))
                return "A dictionary entry contains an unsupported enum value.";
            if (entry.EntryType == DictionaryEntryType.Correction && entry.Replacement is null)
                return "A dictionary correction has no replacement.";
            if (entry.IsRegex)
            {
                try { _ = new Regex(entry.Original, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { return "A dictionary entry contains an invalid regular expression."; }
            }
        }
        if (data.Dictionary.EnabledPackIds.Any(id => !ValidRequired(id) || id.Length > 256))
            return "A dictionary pack identifier is invalid.";
        foreach (var snippet in data.Snippets)
        {
            if (!ValidRequired(snippet.Trigger) || snippet.Trigger.Length > 1_024 || snippet.Replacement is null
                || snippet.Replacement.Length > MaximumTranscriptionTextLength || snippet.Tags is null
                || snippet.Tags.Length > MaximumShortTextLength)
                return "A snippet contains invalid text.";
        }
        foreach (var pair in data.Hotkeys.Bindings)
        {
            if (pair.Key is null || pair.Value is null
                || !HotkeyActions.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                || pair.Value.Count > 16 || pair.Value.Any(value => !ValidHotkey(value)))
                return "The backup contains an invalid hotkey binding.";
        }
        if (data.Hotkeys.Bindings.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Hotkeys.Bindings.Count)
            return "The backup contains duplicate hotkey actions.";
        foreach (var plugin in data.Plugins)
        {
            if (!ValidRequired(plugin.Id) || plugin.Id.Length > 256 || !ValidShort(plugin.Name) || !ValidShort(plugin.Version))
                return "A plugin reference is invalid.";
        }
        foreach (var entry in data.History)
        {
            if (entry.Timestamp == default || entry.RawText is null || entry.FinalText is null || entry.EngineUsed is null
                || entry.RawText.Length > MaximumTranscriptionTextLength || entry.FinalText.Length > MaximumTranscriptionTextLength)
                return "A history entry is invalid.";
            if (!double.IsFinite(entry.DurationSeconds) || entry.DurationSeconds < 0 || entry.DurationSeconds > 31_536_000)
                return "A history entry has an invalid duration.";
            if (!ValidShort(entry.AppName) || !ValidShort(entry.AppProcessName) || !ValidShort(entry.AppUrl)
                || !ValidShort(entry.Language) || !ValidShort(entry.WorkflowName) || !ValidShort(entry.EngineUsed)
                || !ValidShort(entry.ModelUsed) || !ValidShort(entry.TranscriptionTaskUsed))
                return "A history entry contains invalid metadata.";
        }
        if (data.Preferences is { } preferences)
        {
            if (!Enum.IsDefined(preferences.Mode) || !Enum.IsDefined(preferences.HistoryRetentionMode)
                || !Enum.IsDefined(preferences.IndicatorStyle) || !Enum.IsDefined(preferences.OverlayPosition)
                || !Enum.IsDefined(preferences.OverlayLeftWidget) || !Enum.IsDefined(preferences.OverlayRightWidget)
                || !Enum.IsDefined(preferences.EnglishOutputVariant) || !Enum.IsDefined(preferences.GermanOutputVariant))
                return "The backup contains an unsupported preference value.";
            if (preferences.HistoryRetentionMinutes < 1 || preferences.HistoryRetentionMinutes > 10 * 365 * 24 * 60
                || preferences.AudioDuckingLevel is < 0 or > 1
                || preferences.LiveTranscriptionFontSize is < AppSettings.MinLiveTranscriptionFontSize or > AppSettings.MaxLiveTranscriptionFontSize
                || preferences.SilenceAutoStopSeconds is < 1 or > 3_600
                || preferences.PreviewBubbleAutoHideMilliseconds is < AppSettings.MinPreviewBubbleAutoHideMilliseconds or > AppSettings.MaxPreviewBubbleAutoHideMilliseconds
                || preferences.DictationRecoveryRetentionDays is not (-1 or 0 or 1 or 7 or 30 or 60 or 90 or 180))
                return "The backup contains an out-of-range preference value.";
            if (!ValidRequired(preferences.Language) || !ValidShort(preferences.Language)
                || preferences.LanguageHints is null || preferences.LanguageHints.Count > 32
                || preferences.LanguageHints.Any(hint => !ValidRequired(hint) || !ValidShort(hint))
                || !ValidTask(preferences.TranscriptionTask)
                || !ValidShort(preferences.TranslationTargetLanguage)
                || !ValidShort(preferences.LastTranslationTargetLanguage)
                || !ValidRequired(preferences.DictationRecoveryLanguage) || !ValidShort(preferences.DictationRecoveryLanguage)
                || !ValidTask(preferences.DictationRecoveryTask)
                || !ValidRequired(preferences.RecorderOutputFormat) || preferences.RecorderOutputFormat.Length > 32
                || !ValidRequired(preferences.RecorderTrackMode) || preferences.RecorderTrackMode.Length > 32
                || !ValidRequired(preferences.RecorderMicDuckingMode) || preferences.RecorderMicDuckingMode.Length > 32
                || !ValidTask(preferences.RecorderTranscriptionTask)
                || !ValidShort(preferences.RecorderTranslationTargetLanguage)
                || !ValidRequired(preferences.WatchFolderOutputFormat) || preferences.WatchFolderOutputFormat.Length > 32
                || !ValidRequired(preferences.WatchFolderLanguage) || !ValidShort(preferences.WatchFolderLanguage)
                || !ValidRequired(preferences.SelectedIndustryPresetId) || preferences.SelectedIndustryPresetId.Length > 256
                || !ValidUpdateChannel(preferences.UpdateChannel))
                return "The backup contains an invalid portable preference.";
        }

        return null;
    }

    private static IReadOnlyDictionary<BackupCategory, int> CountCategories(SettingsBackupData data) =>
        new Dictionary<BackupCategory, int>
        {
            [BackupCategory.Workflows] = data.Workflows.Count,
            [BackupCategory.Dictionary] = data.Dictionary.Entries.Count + data.Dictionary.EnabledPackIds.Count,
            [BackupCategory.Snippets] = data.Snippets.Count,
            [BackupCategory.Hotkeys] = data.Hotkeys.Bindings.Values.Sum(values => values.Count),
            [BackupCategory.Plugins] = data.Plugins.Count,
            [BackupCategory.History] = data.History.Count,
            [BackupCategory.Preferences] = data.Preferences is null ? 0 : 1
        };

    private static BackupWorkflow ToBackupWorkflow(Workflow workflow) => new()
    {
        Name = workflow.Name,
        IsEnabled = workflow.IsEnabled,
        Template = workflow.Template,
        Trigger = workflow.Trigger,
        Behavior = workflow.Behavior,
        Output = workflow.Output
    };

    private static BackupDictionaryEntry ToBackupDictionaryEntry(DictionaryEntry entry) => new()
    {
        EntryType = entry.EntryType,
        Original = entry.Original,
        Replacement = entry.Replacement,
        CaseSensitive = entry.CaseSensitive,
        IsRegex = entry.IsRegex,
        IsEnabled = entry.IsEnabled,
        Source = entry.Source
    };

    private static BackupSnippet ToBackupSnippet(Snippet snippet) => new()
    {
        Trigger = snippet.Trigger,
        Replacement = snippet.Replacement,
        CaseSensitive = snippet.CaseSensitive,
        IsEnabled = snippet.IsEnabled,
        Tags = snippet.Tags
    };

    private static BackupHotkeys ToBackupHotkeys(AppSettings settings) => new()
    {
        Bindings = CurrentHotkeyBindings(settings)
    };

    private static Dictionary<string, IReadOnlyList<string>> CurrentHotkeyBindings(AppSettings settings) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mainDictation"] = settings.GetMainDictationHotkeys(),
            ["toggleOnly"] = settings.GetToggleOnlyHotkeys(),
            ["holdOnly"] = settings.GetHoldOnlyHotkeys(),
            ["recentTranscriptions"] = settings.GetRecentTranscriptionsHotkeys(),
            ["copyLastTranscription"] = settings.GetCopyLastTranscriptionHotkeys(),
            ["workflowPalette"] = settings.GetWorkflowPaletteHotkeys(),
            ["recorderToggle"] = settings.GetRecorderToggleHotkeys()
        };

    private static BackupHistoryEntry ToBackupHistoryEntry(
        TranscriptionRecord record,
        IReadOnlyDictionary<string, string> workflowNames) => new()
        {
            Timestamp = record.Timestamp,
            RawText = record.RawText,
            FinalText = record.FinalText,
            AppName = record.AppName,
            AppProcessName = record.AppProcessName,
            AppUrl = record.AppUrl,
            DurationSeconds = record.DurationSeconds,
            Language = record.Language,
            WorkflowName = record.WorkflowId is not null && workflowNames.TryGetValue(record.WorkflowId, out var name) ? name : null,
            Status = record.Status,
            EngineUsed = record.EngineUsed,
            ModelUsed = record.ModelUsed,
            TranscriptionTaskUsed = record.TranscriptionTaskUsed,
            UsedTranscriptionFallback = record.UsedTranscriptionFallback
        };

    private static BackupPreferences ToBackupPreferences(AppSettings settings) => new()
    {
        Language = settings.Language,
        LanguageHints = settings.GetLanguageHints(),
        AutoPaste = settings.AutoPaste,
        Mode = settings.Mode,
        HistoryRetentionMode = settings.HistoryRetentionMode,
        HistoryRetentionMinutes = settings.HistoryRetentionMinutes,
        WhisperModeEnabled = settings.WhisperModeEnabled,
        AudioDuckingEnabled = settings.AudioDuckingEnabled,
        AudioDuckingLevel = settings.AudioDuckingLevel,
        PauseMediaDuringRecording = settings.PauseMediaDuringRecording,
        SoundFeedbackEnabled = settings.SoundFeedbackEnabled,
        TranscribeShortQuietClipsAggressively = settings.TranscribeShortQuietClipsAggressively,
        TranscriptionNumberNormalizationEnabled = settings.TranscriptionNumberNormalizationEnabled,
        ShortUtterancePunctuationEnabled = settings.ShortUtterancePunctuationEnabled,
        EnglishOutputVariant = settings.EnglishOutputVariant,
        GermanOutputVariant = settings.GermanOutputVariant,
        LiveTranscriptionEnabled = settings.LiveTranscriptionEnabled,
        OnlineAsrBatchLiveTranscriptionEnabled = settings.OnlineAsrBatchLiveTranscriptionEnabled,
        LiveTranscriptionFontSize = settings.LiveTranscriptionFontSize,
        SilenceAutoStopEnabled = settings.SilenceAutoStopEnabled,
        SilenceAutoStopSeconds = settings.SilenceAutoStopSeconds,
        IndicatorStyle = settings.IndicatorStyle,
        OverlayPosition = settings.OverlayPosition,
        OverlayLeftWidget = settings.OverlayLeftWidget,
        OverlayRightWidget = settings.OverlayRightWidget,
        PreviewBubbleAutoHideMilliseconds = settings.PreviewBubbleAutoHideMilliseconds,
        TranscriptionTask = settings.TranscriptionTask,
        TranslationTargetLanguage = settings.TranslationTargetLanguage,
        LastTranslationTargetLanguage = settings.LastTranslationTargetLanguage,
        DictationRecoveryRetentionDays = settings.DictationRecoveryRetentionDays,
        DictationRecoveryLanguage = settings.DictationRecoveryLanguage,
        DictationRecoveryTask = settings.DictationRecoveryTask,
        DictationRecoveryAutomaticFallbackEnabled = settings.DictationRecoveryAutomaticFallbackEnabled,
        WorkflowRequestRecoveryEnabled = settings.WorkflowRequestRecoveryEnabled,
        RecorderMicEnabled = settings.RecorderMicEnabled,
        RecorderSystemAudioEnabled = settings.RecorderSystemAudioEnabled,
        RecorderOutputFormat = settings.RecorderOutputFormat,
        RecorderTrackMode = settings.RecorderTrackMode,
        RecorderMicDuckingMode = settings.RecorderMicDuckingMode,
        RecorderTranscriptionEnabled = settings.RecorderTranscriptionEnabled,
        RecorderTranscriptionTask = settings.RecorderTranscriptionTask,
        RecorderTranslationTargetLanguage = settings.RecorderTranslationTargetLanguage,
        WatchFolderOutputFormat = settings.WatchFolderOutputFormat,
        WatchFolderDeleteSource = settings.WatchFolderDeleteSource,
        WatchFolderLanguage = settings.WatchFolderLanguage,
        VocabularyBoostingEnabled = settings.VocabularyBoostingEnabled,
        SelectedIndustryPresetId = settings.SelectedIndustryPresetId,
        SaveToHistoryEnabled = settings.SaveToHistoryEnabled,
        MemoryEnabled = settings.MemoryEnabled,
        TargetAppCorrectionLearningEnabled = settings.TargetAppCorrectionLearningEnabled,
        UpdateChannel = settings.UpdateChannel
    };

    private static AppSettings ApplyPreferences(AppSettings settings, BackupPreferences preferences) => settings with
    {
        Language = preferences.Language,
        LanguageHints = AppSettings.NormalizeLanguageHints(preferences.LanguageHints),
        AutoPaste = preferences.AutoPaste,
        Mode = preferences.Mode,
        HistoryRetentionMode = preferences.HistoryRetentionMode,
        HistoryRetentionMinutes = preferences.HistoryRetentionMinutes,
        WhisperModeEnabled = preferences.WhisperModeEnabled,
        AudioDuckingEnabled = preferences.AudioDuckingEnabled,
        AudioDuckingLevel = preferences.AudioDuckingLevel,
        PauseMediaDuringRecording = preferences.PauseMediaDuringRecording,
        SoundFeedbackEnabled = preferences.SoundFeedbackEnabled,
        TranscribeShortQuietClipsAggressively = preferences.TranscribeShortQuietClipsAggressively,
        TranscriptionNumberNormalizationEnabled = preferences.TranscriptionNumberNormalizationEnabled,
        ShortUtterancePunctuationEnabled = preferences.ShortUtterancePunctuationEnabled,
        EnglishOutputVariant = preferences.EnglishOutputVariant,
        GermanOutputVariant = preferences.GermanOutputVariant,
        LiveTranscriptionEnabled = preferences.LiveTranscriptionEnabled,
        OnlineAsrBatchLiveTranscriptionEnabled = preferences.OnlineAsrBatchLiveTranscriptionEnabled,
        LiveTranscriptionFontSize = preferences.LiveTranscriptionFontSize,
        SilenceAutoStopEnabled = preferences.SilenceAutoStopEnabled,
        SilenceAutoStopSeconds = preferences.SilenceAutoStopSeconds,
        IndicatorStyle = preferences.IndicatorStyle,
        OverlayPosition = preferences.OverlayPosition,
        OverlayLeftWidget = preferences.OverlayLeftWidget,
        OverlayRightWidget = preferences.OverlayRightWidget,
        PreviewBubbleAutoHideMilliseconds = preferences.PreviewBubbleAutoHideMilliseconds,
        TranscriptionTask = preferences.TranscriptionTask,
        TranslationTargetLanguage = preferences.TranslationTargetLanguage,
        LastTranslationTargetLanguage = preferences.LastTranslationTargetLanguage,
        DictationRecoveryRetentionDays = preferences.DictationRecoveryRetentionDays,
        DictationRecoveryLanguage = preferences.DictationRecoveryLanguage,
        DictationRecoveryTask = preferences.DictationRecoveryTask,
        DictationRecoveryAutomaticFallbackEnabled = preferences.DictationRecoveryAutomaticFallbackEnabled,
        WorkflowRequestRecoveryEnabled = preferences.WorkflowRequestRecoveryEnabled,
        RecorderMicEnabled = preferences.RecorderMicEnabled,
        RecorderSystemAudioEnabled = preferences.RecorderSystemAudioEnabled,
        RecorderOutputFormat = preferences.RecorderOutputFormat,
        RecorderTrackMode = preferences.RecorderTrackMode,
        RecorderMicDuckingMode = preferences.RecorderMicDuckingMode,
        RecorderTranscriptionEnabled = preferences.RecorderTranscriptionEnabled,
        RecorderTranscriptionTask = preferences.RecorderTranscriptionTask,
        RecorderTranslationTargetLanguage = preferences.RecorderTranslationTargetLanguage,
        WatchFolderOutputFormat = preferences.WatchFolderOutputFormat,
        WatchFolderDeleteSource = preferences.WatchFolderDeleteSource,
        WatchFolderLanguage = preferences.WatchFolderLanguage,
        VocabularyBoostingEnabled = preferences.VocabularyBoostingEnabled,
        SelectedIndustryPresetId = preferences.SelectedIndustryPresetId,
        SaveToHistoryEnabled = preferences.SaveToHistoryEnabled,
        MemoryEnabled = preferences.MemoryEnabled,
        TargetAppCorrectionLearningEnabled = preferences.TargetAppCorrectionLearningEnabled,
        UpdateChannel = preferences.UpdateChannel
    };

    private static bool IsUserAuthoredDictionaryEntry(DictionaryEntry entry) =>
        !entry.Id.StartsWith("pack:", StringComparison.Ordinal);

    private static string DictionaryKey(DictionaryEntry entry) =>
        $"{entry.EntryType}\u001f{entry.Original.Trim().ToUpperInvariant()}\u001f{entry.CaseSensitive}\u001f{entry.IsRegex}";

    private static string DictionaryKey(BackupDictionaryEntry entry) =>
        $"{entry.EntryType}\u001f{entry.Original.Trim().ToUpperInvariant()}\u001f{entry.CaseSensitive}\u001f{entry.IsRegex}";

    private static bool DictionaryValuesEqual(DictionaryEntry existing, BackupDictionaryEntry incoming) =>
        string.Equals(existing.Replacement ?? "", incoming.Replacement ?? "", StringComparison.Ordinal)
        && existing.IsEnabled == incoming.IsEnabled
        && existing.Source == incoming.Source;

    private static bool SnippetValuesEqual(Snippet existing, BackupSnippet incoming) =>
        string.Equals(existing.Replacement, incoming.Replacement, StringComparison.Ordinal)
        && existing.CaseSensitive == incoming.CaseSensitive
        && existing.IsEnabled == incoming.IsEnabled
        && string.Equals(existing.Tags, incoming.Tags, StringComparison.Ordinal);

    private static bool PreferencesEqual(AppSettings settings, BackupPreferences incoming) =>
        string.Equals(
            JsonSerializer.Serialize(ToBackupPreferences(settings), CompactJsonOptions),
            JsonSerializer.Serialize(incoming, CompactJsonOptions),
            StringComparison.Ordinal);

    private static string Fingerprint(BackupWorkflow workflow) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(workflow, CompactJsonOptions)))).ToLowerInvariant();

    private static string HistoryFingerprint(TranscriptionRecord record) => HistoryFingerprint(new BackupHistoryEntry
    {
        Timestamp = record.Timestamp,
        RawText = record.RawText,
        FinalText = record.FinalText,
        AppName = record.AppName,
        AppProcessName = record.AppProcessName,
        AppUrl = record.AppUrl,
        DurationSeconds = record.DurationSeconds,
        Language = record.Language,
        Status = record.Status,
        EngineUsed = record.EngineUsed,
        ModelUsed = record.ModelUsed,
        TranscriptionTaskUsed = record.TranscriptionTaskUsed,
        UsedTranscriptionFallback = record.UsedTranscriptionFallback
    });

    private static string HistoryFingerprint(BackupHistoryEntry entry)
    {
        var canonical = string.Join('\u001f',
            NormalizeUtc(entry.Timestamp).Ticks.ToString(), entry.RawText, entry.FinalText,
            entry.AppName, entry.AppProcessName, entry.AppUrl,
            entry.DurationSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            entry.Language, entry.EngineUsed, entry.ModelUsed, entry.TranscriptionTaskUsed,
            entry.UsedTranscriptionFallback.ToString(), entry.Status.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool ValidRequired(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool ValidShort(string? value) => value is null || value.Length <= MaximumShortTextLength;
    private static bool ValidTask(string? value) =>
        string.Equals(value, "transcribe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "translate", StringComparison.OrdinalIgnoreCase);
    private static bool ValidUpdateChannel(string? value) => value is null
        || string.Equals(value, "stable", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "release-candidate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "daily", StringComparison.OrdinalIgnoreCase);
    private static bool ValidHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        return parts.Length is >= 1 and <= 6 && parts.All(static part => part.Length is > 0 and <= 32);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string ResolveAppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? typeof(BackupRestoreService).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
