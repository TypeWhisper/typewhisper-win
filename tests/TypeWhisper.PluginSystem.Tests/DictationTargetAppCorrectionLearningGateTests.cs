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
}
