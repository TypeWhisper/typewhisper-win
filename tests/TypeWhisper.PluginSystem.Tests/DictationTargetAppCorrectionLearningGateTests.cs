using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class DictationTargetAppCorrectionLearningGateTests
{
    [Fact]
    public void ShouldTrackTargetAppCorrectionLearning_AllowsCommercialPlainPasteByDefault()
    {
        var allowed = DictationViewModel.ShouldTrackTargetAppCorrectionLearning(
            hasCommercialLicense: true,
            AppSettings.Default,
            activeWorkflow: null,
            insertedText: "teh");

        Assert.True(allowed);
        Assert.Null(DictationViewModel.GetTargetAppCorrectionLearningSkipReason(
            hasCommercialLicense: true,
            AppSettings.Default,
            activeWorkflow: null,
            insertedText: "teh"));
    }

    [Fact]
    public void ShouldTrackTargetAppCorrectionLearning_AllowsCommercialPlainPasteWhenEnabled()
    {
        var settings = AppSettings.Default with
        {
            TargetAppCorrectionLearningEnabled = true
        };

        var allowed = DictationViewModel.ShouldTrackTargetAppCorrectionLearning(
            hasCommercialLicense: true,
            settings,
            activeWorkflow: null,
            insertedText: "teh");

        Assert.True(allowed);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ShouldTrackTargetAppCorrectionLearning_BlocksMissingLicenseDisabledSettingOrClipboardFallback(
        bool hasCommercialLicense,
        bool learningEnabled,
        bool autoPaste)
    {
        var settings = AppSettings.Default with
        {
            TargetAppCorrectionLearningEnabled = learningEnabled,
            AutoPaste = autoPaste
        };

        var allowed = DictationViewModel.ShouldTrackTargetAppCorrectionLearning(
            hasCommercialLicense,
            settings,
            activeWorkflow: null,
            insertedText: "teh");

        Assert.False(allowed);
    }

    [Theory]
    [InlineData(false, true, true, "no_commercial_license")]
    [InlineData(true, false, true, "learning_disabled")]
    [InlineData(true, true, false, "auto_paste_disabled")]
    public void GetTargetAppCorrectionLearningSkipReason_ExplainsGateBlocks(
        bool hasCommercialLicense,
        bool learningEnabled,
        bool autoPaste,
        string expectedReason)
    {
        var settings = AppSettings.Default with
        {
            TargetAppCorrectionLearningEnabled = learningEnabled,
            AutoPaste = autoPaste
        };

        var reason = DictationViewModel.GetTargetAppCorrectionLearningSkipReason(
            hasCommercialLicense,
            settings,
            activeWorkflow: null,
            insertedText: "teh");

        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void ShouldTrackTargetAppCorrectionLearning_BlocksActionPluginOutput()
    {
        var settings = AppSettings.Default with
        {
            TargetAppCorrectionLearningEnabled = true
        };
        var workflow = new Workflow
        {
            Id = "action",
            Name = "Action",
            Template = WorkflowTemplate.Custom,
            Trigger = new WorkflowTrigger { Kind = WorkflowTriggerKind.Manual },
            Output = new WorkflowOutput { TargetActionPluginId = "plugin.action" }
        };

        var allowed = DictationViewModel.ShouldTrackTargetAppCorrectionLearning(
            hasCommercialLicense: true,
            settings,
            workflow,
            insertedText: "teh");

        Assert.False(allowed);
        Assert.Equal("action_plugin_output", DictationViewModel.GetTargetAppCorrectionLearningSkipReason(
            hasCommercialLicense: true,
            settings,
            workflow,
            insertedText: "teh"));
    }

    [Fact]
    public void ShouldTrackTargetAppCorrectionLearning_BlocksNonPlainWorkflowOutput()
    {
        var settings = AppSettings.Default with
        {
            TargetAppCorrectionLearningEnabled = true
        };
        var workflow = new Workflow
        {
            Id = "json",
            Name = "JSON",
            Template = WorkflowTemplate.Custom,
            Trigger = new WorkflowTrigger { Kind = WorkflowTriggerKind.Manual },
            Output = new WorkflowOutput { Format = "JSON" }
        };

        var allowed = DictationViewModel.ShouldTrackTargetAppCorrectionLearning(
            hasCommercialLicense: true,
            settings,
            workflow,
            insertedText: "teh");

        Assert.False(allowed);
        Assert.Equal("non_plain_output", DictationViewModel.GetTargetAppCorrectionLearningSkipReason(
            hasCommercialLicense: true,
            settings,
            workflow,
            insertedText: "teh"));
    }

    [Fact]
    public void ShouldTrackTargetAppCorrectionLearning_BlocksLongInsertedText()
    {
        var settings = AppSettings.Default with
        {
            TargetAppCorrectionLearningEnabled = true
        };

        var allowed = DictationViewModel.ShouldTrackTargetAppCorrectionLearning(
            hasCommercialLicense: true,
            settings,
            activeWorkflow: null,
            insertedText: new string('a', 2049));

        Assert.False(allowed);
        Assert.Equal("text_too_long", DictationViewModel.GetTargetAppCorrectionLearningSkipReason(
            hasCommercialLicense: true,
            settings,
            activeWorkflow: null,
            insertedText: new string('a', 2049)));
    }

    [Fact]
    public async Task CaptureTargetAppCorrectionBaselineAsync_RetriesUntilInsertedTextIsObserved()
    {
        var observations = new Queue<TargetAppTextObservation?>([
            new TargetAppTextObservation("editable", "", new IntPtr(42)),
            new TargetAppTextObservation("editable", "alpha watre beta", new IntPtr(42))
        ]);
        var delays = new List<TimeSpan>();

        var result = await DictationViewModel.CaptureTargetAppCorrectionBaselineAsync(
            insertedText: "alpha watre beta",
            capture: () => observations.Dequeue(),
            retryDelays: [TimeSpan.FromMilliseconds(10)],
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(result.Baseline);
        Assert.Null(result.SkipReason);
        Assert.Equal("alpha watre beta", result.Baseline!.Value);
        Assert.Equal([TimeSpan.FromMilliseconds(10)], delays);
    }

    [Fact]
    public async Task CaptureTargetAppCorrectionBaselineAsync_PassesInsertedTextToCapture()
    {
        string? preferredText = null;

        var result = await DictationViewModel.CaptureTargetAppCorrectionBaselineAsync(
            insertedText: "alpha watre beta",
            capture: text =>
            {
                preferredText = text;
                return new TargetAppTextObservation("editable", "alpha watre beta", new IntPtr(42));
            },
            retryDelays: [],
            delayAsync: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.NotNull(result.Baseline);
        Assert.Equal("alpha watre beta", preferredText);
    }

    [Fact]
    public async Task CaptureTargetAppCorrectionBaselineAsync_ThrowsWhenCancelledAfterSlowCapture()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DictationViewModel.CaptureTargetAppCorrectionBaselineAsync(
                insertedText: "alpha watre beta",
                capture: _ =>
                {
                    cts.Cancel();
                    return new TargetAppTextObservation("editable", "alpha watre beta", new IntPtr(42));
            },
            retryDelays: [],
            delayAsync: (_, _) => Task.CompletedTask,
            cts.Token));
    }

    [Fact]
    public async Task CaptureTargetAppCorrectionBaselineWithTimeoutAsync_CancelsInnerCaptureWhenTimeoutElapses()
    {
        using var cts = new CancellationTokenSource();
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken retryToken = default;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            DictationViewModel.CaptureTargetAppCorrectionBaselineWithTimeoutAsync(
                insertedText: "alpha watre beta",
                capture: _ => null,
                retryDelays: [TimeSpan.FromMinutes(1)],
                delayAsync: (_, token) =>
                {
                    retryToken = token;
                    delayStarted.SetResult();
                    token.Register(() => delayCancelled.SetResult());
                    return delayCancelled.Task;
                },
                timeout: TimeSpan.FromMilliseconds(10),
                cts.Token));

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(retryToken.IsCancellationRequested);
        await delayCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CaptureTargetAppCorrectionBaselineAsync_ReturnsReasonWhenInsertedTextNeverAppears()
    {
        var result = await DictationViewModel.CaptureTargetAppCorrectionBaselineAsync(
            insertedText: "alpha watre beta",
            capture: () => new TargetAppTextObservation("editable", "alpha beta", new IntPtr(42)),
            retryDelays: [TimeSpan.Zero],
            delayAsync: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Null(result.Baseline);
        Assert.Equal("inserted_text_not_observed", result.SkipReason);
    }

    [Fact]
    public void TargetAppCorrectionBaselineCapture_TreatsUnauthorizedAccessAsCaptureError()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var captureBlock = TestFile.ExtractBlock(
            source,
            "private async Task<TargetAppCorrectionLearningStartResult> TryStartTargetAppCorrectionLearningAsync",
            5200);

        Assert.Contains("catch (UnauthorizedAccessException)", captureBlock);
        Assert.Contains("return new TargetAppCorrectionLearningStartResult(false, \"capture_error\");", captureBlock);
    }

    [Fact]
    public void ProcessSingleJob_StartsTargetAppCorrectionLearningInBackground()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var processBlock = TestFile.ExtractBlock(
            source,
            "private async Task ProcessSingleJobAsync",
            30000);

        Assert.Contains("StartTargetAppCorrectionLearningInBackground(job, insertionText, insertResult);", processBlock);
        Assert.DoesNotContain("await StartTargetAppCorrectionLearningIfEligibleAsync(job, insertionText, insertResult, ct);", processBlock);
    }

    [Fact]
    public void BackgroundTargetAppCorrectionLearning_UsesTimeoutAndDeepCapture()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var backgroundBlock = TestFile.ExtractBlock(
            source,
            "private void StartTargetAppCorrectionLearningInBackground",
            4200);

        Assert.Contains("TargetAppCorrectionBackgroundStartTimeout", source);
        Assert.Contains("cts.CancelAfter(TargetAppCorrectionBackgroundStartTimeout);", backgroundBlock);
        Assert.Contains("_targetAppCorrectionLearningCts = cts;", backgroundBlock);
        Assert.Contains(".CaptureDeep(", backgroundBlock);
        Assert.Contains("allowDescendantScan: true", backgroundBlock);
        Assert.Contains("Task.Run", backgroundBlock);
    }

    [Fact]
    public void AutomationTargetAppCorrectionLearning_DoesNotUseSlowDescendantScan()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var startBlock = TestFile.ExtractBlock(
            source,
            "private async Task<TargetAppCorrectionLearningStartResult> TryStartTargetAppCorrectionLearningAsync",
            3200);

        Assert.Contains(".CaptureDeep(", startBlock);
        Assert.Contains("allowDescendantScan: false", startBlock);
        Assert.DoesNotContain("allowDescendantScan: true", startBlock);
        Assert.Contains("TargetAppCorrectionAutomationStartTimeout", source);
        Assert.Contains("CaptureTargetAppCorrectionBaselineWithTimeoutAsync", startBlock);
        Assert.Contains("catch (TimeoutException)", startBlock);
    }

    [Fact]
    public void TargetAppCorrectionLearningTask_DisposesLinkedCancellationSource()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var taskBlock = TestFile.ExtractBlock(
            source,
            "var trackingTask = Task.Run",
            4200);
        var cancelBlock = TestFile.ExtractBlock(
            source,
            "private void CancelTargetAppCorrectionLearning()",
            900);

        Assert.Contains("private readonly object _targetAppCorrectionLearningSync = new();", source);
        Assert.Contains("finally", taskBlock);
        Assert.Contains("lock (_targetAppCorrectionLearningSync)", taskBlock);
        Assert.Contains("ReferenceEquals(_targetAppCorrectionLearningCts, cts)", taskBlock);
        Assert.Contains("_targetAppCorrectionLearningCts = null;", taskBlock);
        Assert.Contains("_targetAppCorrectionLearningTask = trackingTask;", taskBlock);
        Assert.Contains("ContinueWith", taskBlock);
        Assert.Contains("_targetAppCorrectionLearningTask = null;", taskBlock);
        Assert.Contains("TaskContinuationOptions.ExecuteSynchronously", taskBlock);
        Assert.Contains("TaskScheduler.Default", taskBlock);
        Assert.Contains("cts.Dispose();", taskBlock);
        Assert.Contains("lock (_targetAppCorrectionLearningSync)", cancelBlock);
        Assert.Contains("_targetAppCorrectionLearningCts?.Cancel();", cancelBlock);
    }
}
