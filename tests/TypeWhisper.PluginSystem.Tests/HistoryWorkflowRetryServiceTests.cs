using System.IO;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class HistoryWorkflowRetryServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"typewhisper-history-retry-{Guid.NewGuid():N}");

    public HistoryWorkflowRetryServiceTests() => Directory.CreateDirectory(_tempDirectory);

    [Fact]
    public void ResolveInitialWorkflow_PrefersIdAndUsesOnlyUniqueLegacyName()
    {
        using var store = new DictationRecoveryAudioStore(Path.Combine(_tempDirectory, "recovery"));
        var first = CreateWorkflow("first", "Shared");
        var second = CreateWorkflow("second", "Shared");
        var unique = CreateWorkflow("unique", "Unique");
        var service = CreateService(store, [first, second, unique], _ => Task.FromResult("done"));

        Assert.Same(second, service.ResolveInitialWorkflow(CreateRecord() with { WorkflowId = "second", ProfileName = "Shared" }));
        Assert.Same(unique, service.ResolveInitialWorkflow(CreateRecord() with { ProfileName = "Unique" }));
        Assert.Null(service.ResolveInitialWorkflow(CreateRecord() with { ProfileName = "Shared" }));
        Assert.Null(service.ResolveInitialWorkflow(CreateRecord() with { WorkflowId = "missing", ProfileName = "Unique" }));
    }

    [Fact]
    public void EligibleWorkflows_IncludeDisabledPromptWorkflowsAndExcludeActionOnlyWorkflows()
    {
        using var store = new DictationRecoveryAudioStore(Path.Combine(_tempDirectory, "recovery-eligibility"));
        var disabledPrompt = CreateWorkflow("prompt", "Prompt") with { IsEnabled = false };
        var actionOnly = new Workflow
        {
            Id = "action",
            Name = "Action only",
            IsEnabled = true,
            Template = WorkflowTemplate.Custom,
            Trigger = WorkflowTrigger.Manual(),
            Behavior = new WorkflowBehavior { ProviderOverride = "none" },
            Output = new WorkflowOutput { TargetActionPluginId = "test-action" }
        };
        var service = CreateService(store, [actionOnly, disabledPrompt], _ => Task.FromResult("done"));

        var eligible = service.GetEligibleWorkflows();

        Assert.Equal(disabledPrompt, Assert.Single(eligible));
    }

    [Fact]
    public async Task RetryAsync_ReplacesSameRecordAndDeletesRecoveryAfterPersistence()
    {
        await using var store = new DictationRecoveryAudioStore(Path.Combine(_tempDirectory, "recovery-success"));
        var recovery = await CreateRecoveryAsync(store);
        var workflow = CreateWorkflow("workflow", "Cleanup");
        var history = CreateHistory("success-history.json");
        var original = CreateRecord() with
        {
            WorkflowId = workflow.Id,
            ProfileName = workflow.Name,
            RecoveryAudioFileName = recovery.FileName
        };
        Assert.True(history.TryAddRecord(original));
        var service = CreateService(store, [workflow], _ => Task.FromResult("clean result"), history);

        var updated = await service.RetryAsync(original.Id, workflow.Id, null, CancellationToken.None);

        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("clean result", updated.FinalText);
        Assert.Equal(TranscriptionRecordStatus.Succeeded, updated.Status);
        Assert.Null(updated.WorkflowFailureMessage);
        Assert.Null(updated.RecoveryAudioFileName);
        Assert.Empty(store.Recordings);
        var persisted = Assert.Single(history.Records);
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal("clean result", persisted.FinalText);
    }

    [Fact]
    public async Task RetryAsync_FailureUpdatesSameRecordWithoutDeletingRecoveryOrRawText()
    {
        await using var store = new DictationRecoveryAudioStore(Path.Combine(_tempDirectory, "recovery-failure"));
        var recovery = await CreateRecoveryAsync(store);
        var workflow = CreateWorkflow("workflow", "Cleanup");
        var history = CreateHistory("failure-history.json");
        var original = CreateRecord() with { RecoveryAudioFileName = recovery.FileName };
        Assert.True(history.TryAddRecord(original));
        var service = CreateService(
            store,
            [workflow],
            _ => Task.FromException<string>(new InvalidOperationException("provider unavailable")),
            history);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RetryAsync(original.Id, workflow.Id, null, CancellationToken.None));

        var failed = Assert.Single(history.Records);
        Assert.Equal(original.Id, failed.Id);
        Assert.Equal(original.RawText, failed.RawText);
        Assert.Empty(failed.FinalText);
        Assert.Equal(TranscriptionRecordStatus.WorkflowPostProcessingFailed, failed.Status);
        Assert.Equal("provider unavailable", failed.WorkflowFailureMessage);
        Assert.Equal(recovery.FileName, failed.RecoveryAudioFileName);
        Assert.Single(store.Recordings);
    }

    [Fact]
    public async Task RetryAsync_UserCancellationLeavesExistingFailureUnchanged()
    {
        await using var store = new DictationRecoveryAudioStore(Path.Combine(_tempDirectory, "recovery-cancel"));
        var workflow = CreateWorkflow("workflow", "Cleanup");
        var history = CreateHistory("cancel-history.json");
        var original = CreateRecord();
        Assert.True(history.TryAddRecord(original));
        var service = CreateService(store, [workflow], _ => Task.FromCanceled<string>(new(true)), history);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RetryAsync(original.Id, workflow.Id, null, new CancellationToken(true)));

        Assert.Equal(original, Assert.Single(history.Records));
    }

    [Fact]
    public void SanitizeFailure_RemovesDictatedTextAndProviderDetails()
    {
        Assert.Equal(
            "failure for [dictated text]",
            HistoryWorkflowRetryService.SanitizeFailure(
                new InvalidOperationException("failure for raw text remains"),
                "raw text remains"));
        Assert.Equal(
            "The workflow provider rejected the request.",
            HistoryWorkflowRetryService.SanitizeFailure(
                new PluginRequestException(
                    "provider echoed a private prompt",
                    PluginRequestFailureKind.InvalidRequest)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
    }

    private HistoryWorkflowRetryService CreateService(
        DictationRecoveryAudioStore store,
        IReadOnlyList<Workflow> workflows,
        Func<WorkflowPostProcessingRequest, Task<string>> process,
        IHistoryService? history = null)
    {
        history ??= CreateHistory($"history-{Guid.NewGuid():N}.json");
        return new HistoryWorkflowRetryService(
            history,
            new FakeWorkflowService(workflows),
            new FakeSettingsService(AppSettings.Default),
            new FakePostProcessingService(process),
            store);
    }

    private HistoryService CreateHistory(string fileName) =>
        new(Path.Combine(_tempDirectory, fileName), Path.Combine(_tempDirectory, "audio"));

    private static async Task<RecoveryRecordingDescriptor> CreateRecoveryAsync(DictationRecoveryAudioStore store)
    {
        var id = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(id, Enumerable.Repeat(0.25f, 1600).ToArray());
        var lease = Assert.IsType<RecoveryRecordingLease>(await store.FinalizeRecordingAsync(id));
        return Assert.IsType<RecoveryRecordingDescriptor>(await lease.PreserveAsync());
    }

    private static TranscriptionRecord CreateRecord() => new()
    {
        Id = "same-record",
        Timestamp = DateTime.UtcNow,
        RawText = "raw text remains",
        FinalText = string.Empty,
        Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed,
        WorkflowFailureMessage = "first failure",
        Language = "en",
        TranscriptionTaskUsed = "Transcribe"
    };

    private static Workflow CreateWorkflow(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Template = WorkflowTemplate.Custom,
        Trigger = WorkflowTrigger.Manual(),
        Behavior = new WorkflowBehavior
        {
            Settings = new Dictionary<string, string> { ["instruction"] = "Clean the text" }
        }
    };

    private sealed class FakePostProcessingService(
        Func<WorkflowPostProcessingRequest, Task<string>> process) : IWorkflowPostProcessingService
    {
        public async Task<WorkflowPostProcessingResult> ProcessAsync(
            WorkflowPostProcessingRequest request,
            Func<string, Task>? statusCallback,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new WorkflowPostProcessingResult(await process(request), true);
        }
    }

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class FakeWorkflowService(IReadOnlyList<Workflow> workflows) : IWorkflowService
    {
        public IReadOnlyList<Workflow> Workflows { get; } = workflows;
        public event Action? WorkflowsChanged
        {
            add { }
            remove { }
        }
        public void AddWorkflow(Workflow workflow) => throw new NotSupportedException();
        public void UpdateWorkflow(Workflow workflow) => throw new NotSupportedException();
        public void DeleteWorkflow(string id) => throw new NotSupportedException();
        public void ToggleWorkflow(string id) => throw new NotSupportedException();
        public void Reorder(IReadOnlyList<string> orderedIds) => throw new NotSupportedException();
        public int NextSortOrder() => Workflows.Count;
        public Workflow? GetWorkflow(string id) => Workflows.FirstOrDefault(workflow => workflow.Id == id);
        public WorkflowMatchResult? MatchWorkflow(string? processName, string? url) => null;
        public WorkflowMatchResult? ForceMatch(string workflowId) => null;
    }
}
