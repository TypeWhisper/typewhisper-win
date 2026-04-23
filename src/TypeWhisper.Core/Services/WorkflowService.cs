using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

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

    public WorkflowService(string filePath)
    {
        _filePath = filePath;
    }

    public IReadOnlyList<Workflow> Workflows
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? WorkflowsChanged;

    public void AddWorkflow(Workflow workflow)
    {
        EnsureCacheLoaded();
        var created = workflow with
        {
            CreatedAt = workflow.CreatedAt == default ? DateTime.UtcNow : workflow.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };
        _cache.Add(created);
        SortCache();
        SaveToDisk();
        WorkflowsChanged?.Invoke();
    }

    public void UpdateWorkflow(Workflow workflow)
    {
        EnsureCacheLoaded();
        var updated = workflow with { UpdatedAt = DateTime.UtcNow };
        var idx = _cache.FindIndex(w => w.Id == workflow.Id);
        if (idx >= 0) _cache[idx] = updated;
        SortCache();
        SaveToDisk();
        WorkflowsChanged?.Invoke();
    }

    public void DeleteWorkflow(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(w => w.Id == id);
        SaveToDisk();
        WorkflowsChanged?.Invoke();
    }

    public void ToggleWorkflow(string id)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(w => w.Id == id);
        if (idx < 0) return;

        _cache[idx] = _cache[idx] with
        {
            IsEnabled = !_cache[idx].IsEnabled,
            UpdatedAt = DateTime.UtcNow
        };
        SaveToDisk();
        WorkflowsChanged?.Invoke();
    }

    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        EnsureCacheLoaded();
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
        SaveToDisk();
        WorkflowsChanged?.Invoke();
    }

    public int NextSortOrder()
    {
        EnsureCacheLoaded();
        return _cache.Count == 0 ? 0 : _cache.Max(w => w.SortOrder) + 1;
    }

    public Workflow? GetWorkflow(string id)
    {
        EnsureCacheLoaded();
        return _cache.FirstOrDefault(w => w.Id == id);
    }

    public WorkflowMatchResult? ForceMatch(string workflowId)
    {
        EnsureCacheLoaded();
        var workflow = _cache.FirstOrDefault(w => w.Id == workflowId && w.IsEnabled);
        return workflow is null
            ? null
            : new WorkflowMatchResult(workflow, WorkflowMatchKind.ManualOverride, null, 0, false);
    }

    public WorkflowMatchResult? MatchWorkflow(string? processName, string? url)
    {
        EnsureCacheLoaded();
        var enabled = _cache.Where(w => w.IsEnabled && w.Trigger.HasValues).ToList();
        var domain = ExtractHost(url);

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var websiteMatches = enabled
                .Where(w => w.Trigger.Kind == WorkflowTriggerKind.Website
                            && w.Trigger.WebsitePatterns.Any(pattern => MatchesUrlPattern(domain, pattern)))
                .ToList();
            if (BestMatch(websiteMatches, WorkflowMatchKind.Website, domain) is { } websiteResult)
                return websiteResult;
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            var appMatches = enabled
                .Where(w => w.Trigger.Kind == WorkflowTriggerKind.App
                            && w.Trigger.ProcessNames.Any(name =>
                                processName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (BestMatch(appMatches, WorkflowMatchKind.App, null) is { } appResult)
                return appResult;
        }

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

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonSerializer.Deserialize<List<Workflow>>(json, JsonOptions) ?? [];
            }
        }
        catch
        {
            _cache = [];
        }

        SortCache();
        _cacheLoaded = true;
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
