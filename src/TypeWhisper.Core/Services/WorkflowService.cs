using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides workflow service behavior.
/// </summary>
public sealed class WorkflowService : IWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private List<Workflow> _cache = [];
    private bool _cacheLoaded;

    /// <summary>
    /// Initializes a new instance of the WorkflowService class.
    /// </summary>
    public WorkflowService(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Gets why the workflow file could not be read or parsed, or null when it loaded or does not exist yet.
    /// </summary>
    /// <remarks>
    /// While set, <see cref="Workflows"/> is empty and every mutation is refused, so a locked or corrupt file is
    /// never replaced by that empty list. <see cref="Reload"/> clears it once the file can be read again.
    /// </remarks>
    public Exception? LoadError { get; private set; }

    /// <summary>
    /// Gets why the most recent mutation was not written to disk, or null when it was persisted.
    /// </summary>
    public Exception? LastSaveError { get; private set; }

    /// <summary>
    /// Reads the workflow file again, for example after a sync client released it, and reports whether it loaded.
    /// </summary>
    public bool Reload()
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        _cacheLoaded = false;
        EnsureCacheLoaded();
        if (LoadError is not null) return false;

        WorkflowsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Gets the configured workflows in display order.
    /// </summary>
    /// <summary>
    /// Gets the configured workflows in display order.
    /// </summary>
    public IReadOnlyList<Workflow> Workflows
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    /// <summary>
    /// Raised when workflows changes.
    /// </summary>
    public event Action? WorkflowsChanged;

    /// <summary>
    /// Adds workflow.
    /// </summary>
    public void AddWorkflow(Workflow workflow)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        var created = workflow with
        {
            CreatedAt = workflow.CreatedAt == default ? DateTime.UtcNow : workflow.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
        _cache.Add(created);
        SortCache();
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Updates workflow.
    /// </summary>
    public void UpdateWorkflow(Workflow workflow)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        var updated = workflow with { UpdatedAt = DateTime.UtcNow };
        var idx = _cache.FindIndex(w => w.Id == workflow.Id);
        if (idx >= 0) _cache[idx] = updated;
        SortCache();
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Deletes workflow.
    /// </summary>
    public void DeleteWorkflow(string id)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        _cache.RemoveAll(w => w.Id == id);
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Toggles workflow.
    /// </summary>
    public void ToggleWorkflow(string id)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        var idx = _cache.FindIndex(w => w.Id == id);
        if (idx < 0) return;

        _cache[idx] = _cache[idx] with
        {
            IsEnabled = !_cache[idx].IsEnabled,
            UpdatedAt = DateTime.UtcNow
        };
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Reorders
    /// </summary>
    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var idx = _cache.FindIndex(w => w.Id == orderedIds[i]);
            if (idx >= 0)
            {
                _cache[idx] = _cache[idx] with
                {
                    SortOrder = i,
                    UpdatedAt = DateTime.UtcNow
                };
            }
        }

        SortCache();
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Performs next sort order.
    /// </summary>
    public int NextSortOrder()
    {
        EnsureCacheLoaded();
        return _cache.Count == 0 ? 0 : _cache.Max(w => w.SortOrder) + 1;
    }

    /// <summary>
    /// Returns workflow.
    /// </summary>
    public Workflow? GetWorkflow(string id)
    {
        EnsureCacheLoaded();
        return _cache.FirstOrDefault(w => w.Id == id);
    }

    /// <summary>
    /// Performs force match.
    /// </summary>
    public WorkflowMatchResult? ForceMatch(string workflowId)
    {
        EnsureCacheLoaded();
        var workflow = _cache.FirstOrDefault(w => w.Id == workflowId && w.IsEnabled);
        return workflow is null
            ? null
            : new WorkflowMatchResult(workflow, WorkflowMatchKind.ManualOverride, null, 0, false);
    }

    /// <summary>
    /// Performs match workflow.
    /// </summary>
    public WorkflowMatchResult? MatchWorkflow(string? processName, string? url)
    {
        EnsureCacheLoaded();
        return MatchSnapshot(_cache, processName, url);
    }

    /// <summary>Matches an explicit workflow snapshot using the same precedence as the persisted catalog.</summary>
    public static WorkflowMatchResult? MatchSnapshot(IEnumerable<Workflow> workflows, string? processName, string? url)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        var enabled = workflows.Where(w => w.IsEnabled && w.Trigger.HasValues).ToList();
        var domain = ExtractHost(url);

        if (!string.IsNullOrWhiteSpace(processName) && !string.IsNullOrWhiteSpace(domain))
        {
            var appAndWebsiteMatches = enabled
                .Where(w => w.Trigger.IsAutomatic
                            && w.Trigger.HasAppBindings
                            && w.Trigger.HasWebsiteBindings
                            && w.Trigger.ProcessNames.Any(name =>
                                processName.Equals(name, StringComparison.OrdinalIgnoreCase))
                            && w.Trigger.WebsitePatterns.Any(pattern => MatchesUrlPattern(domain, pattern)))
                .ToList();
            if (BestMatch(appAndWebsiteMatches, WorkflowMatchKind.AppAndWebsite, domain) is { } appAndWebsiteResult)
                return appAndWebsiteResult;
        }

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var websiteMatches = enabled
                .Where(w => w.Trigger.IsAutomatic
                            && (!w.Trigger.HasAppBindings
                                || w.Trigger.ContextMatchMode == WorkflowContextMatchMode.Any)
                            && w.Trigger.WebsitePatterns.Any(pattern => MatchesUrlPattern(domain, pattern)))
                .ToList();
            if (BestMatch(websiteMatches, WorkflowMatchKind.Website, domain) is { } websiteResult)
                return websiteResult;
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            var appMatches = enabled
                .Where(w => w.Trigger.IsAutomatic
                            && (!w.Trigger.HasWebsiteBindings
                                || w.Trigger.ContextMatchMode == WorkflowContextMatchMode.Any)
                            && w.Trigger.ProcessNames.Any(name =>
                                processName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (BestMatch(appMatches, WorkflowMatchKind.App, null) is { } appResult)
                return appResult;
        }

        var globalMatches = enabled
            .Where(w => w.Trigger.Kind == WorkflowTriggerKind.Global)
            .ToList();
        if (BestMatch(globalMatches, WorkflowMatchKind.GlobalFallback, null) is { } globalResult)
            return globalResult;

        return null;
    }

    private static WorkflowMatchResult? BestMatch(
        IReadOnlyList<Workflow> matches,
        WorkflowMatchKind kind,
        string? matchedDomain)
    {
        var sorted = matches
            .OrderBy(w => w.SortOrder)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sorted.Count == 0)
            return null;

        var best = sorted[0];
        var secondSortOrder = sorted.Count > 1 ? sorted[1].SortOrder : (int?)null;
        return new WorkflowMatchResult(
            best,
            kind,
            matchedDomain,
            Math.Max(sorted.Count - 1, 0),
            secondSortOrder.HasValue && best.SortOrder < secondSortOrder.Value);
    }

    private static string? ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return NormalizeHost(uri.Host);

        return NormalizeHost(url);
    }

    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return trimmed[4..];
        return trimmed;
    }

    private static bool MatchesUrlPattern(string host, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var normalizedHost = NormalizeHost(host);
        var normalizedPattern = NormalizeHost(pattern);

        if (normalizedPattern.StartsWith("*."))
        {
            var suffix = normalizedPattern[1..];
            return normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || normalizedHost.Equals(normalizedPattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return normalizedHost.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase)
               || normalizedHost.EndsWith("." + normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    private void SortCache()
    {
        _cache = _cache
            .OrderBy(w => w.SortOrder)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        LoadError = null;
        try
        {
            var json = File.ReadAllText(_filePath);
            // A zero-length file holds nothing to protect, so it counts as an empty catalog like a missing one.
            _cache = string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<Workflow>>(json, JsonOptions) ?? [];
        }
        catch (FileNotFoundException) { _cache = []; }
        catch (DirectoryNotFoundException) { _cache = []; }
        catch (Exception ex)
        {
            // The file exists but cannot be trusted; keep it off limits instead of treating it as empty.
            LoadError = ex;
            _cache = [];
        }

        SortCache();
        _cacheLoaded = true;
    }

    // Mutations are refused rather than thrown while the file is unreadable: dictation and sync reach them, and an
    // exception there would abort the dictation or the sync cycle. The cache is empty in that state, so writing it
    // would replace the user's workflows with an almost empty list. Callers can inspect LoadError and LastSaveError.
    private bool TryBeginMutation(out List<Workflow> rollback)
    {
        EnsureCacheLoaded();
        if (LoadError is null)
        {
            rollback = _cache.ToList();
            return true;
        }

        LastSaveError = new InvalidOperationException(
            "The workflow file could not be loaded, so changes are not saved until it loads again.", LoadError);
        rollback = [];
        return false;
    }

    // A failed write restores the previous list and raises no event, so the cache keeps matching the file on disk.
    private bool TryCommitMutation(List<Workflow> rollback)
    {
        if (SaveToDisk(_cache))
        {
            WorkflowsChanged?.Invoke();
            return true;
        }

        _cache = rollback;
        return false;
    }

    /// <inheritdoc />
    public bool TryReplaceAll(IReadOnlyList<Workflow> workflows)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out _)) return false;
        var replacement = workflows.ToList();
        replacement = replacement
            .OrderBy(workflow => workflow.SortOrder)
            .ThenBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!SaveToDisk(replacement))
            return false;

        _cache = replacement;
        WorkflowsChanged?.Invoke();
        return true;
    }

    private bool SaveToDisk(IReadOnlyList<Workflow> workflows)
    {
        if (!AtomicFileWriter.TryWriteAllText(_filePath, JsonSerializer.Serialize(workflows, JsonOptions), out var error))
        {
            LastSaveError = error;
            return false;
        }

        LastSaveError = null;
        return true;
    }
}
