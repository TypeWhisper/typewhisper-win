using TypeWhisper.PluginSDK.Models;
using TypeWhisper.WinUI;
using Xunit;

public sealed class DictationTargetStepsTests
{
    private sealed class ManualTime : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Reports every new step and lets the app show it for a minute, like a game add-on's bar.
    private static List<DictationTargetPhase> Run(DictationTargetSteps steps, params (DictationPhase Phase, bool Recording, bool Delivered)[] phases)
    {
        var reported = new List<DictationTargetPhase>();
        foreach (var (phase, recording, delivered) in phases)
        {
            if (steps.Next(phase, recording, delivered) is not { } step) continue;
            reported.Add(step);
            steps.Reported(step, DateTimeOffset.UtcNow.AddMinutes(1));
        }
        return reported;
    }

    [Fact]
    public void Dictation_ReportsEachStepOnce_InOrder()
    {
        var reported = Run(new(),
            (DictationPhase.Recording, true, false), (DictationPhase.Recording, true, false),
            (DictationPhase.Processing, false, false), (DictationPhase.LoadingModel, false, false),
            (DictationPhase.Completed, false, true), (DictationPhase.Idle, false, true));

        Assert.Equal([DictationTargetPhase.Recording, DictationTargetPhase.Transcribing, DictationTargetPhase.Delivered], reported);
    }

    [Fact]
    public void ModelLoading_BelongsToTheRecordingWhileTheMicrophoneIsOpen()
    {
        Assert.Equal(DictationTargetPhase.Recording, DictationTargetSteps.StepFor(DictationPhase.LoadingModel, recording: true, delivered: false));
        Assert.Equal(DictationTargetPhase.Transcribing, DictationTargetSteps.StepFor(DictationPhase.LoadingModel, recording: false, delivered: false));
    }

    [Theory]
    [InlineData(nameof(DictationPhase.Idle))]
    [InlineData(nameof(DictationPhase.Error))]
    [InlineData(nameof(DictationPhase.Copied))]
    [InlineData(nameof(DictationPhase.Completed))]
    [InlineData(nameof(DictationPhase.Configuring))]
    public void CancelFailureCopyOrActionWithoutText_StopsOnce(string end)
    {
        var reported = Run(new(),
            (DictationPhase.Recording, true, false), (Enum.Parse<DictationPhase>(end), false, false),
            (DictationPhase.Idle, false, false), (DictationPhase.Recording, true, false));

        Assert.Equal([DictationTargetPhase.Recording, DictationTargetPhase.Stopped], reported);
    }

    [Fact]
    public void ADictationThatEndsBeforeItsFirstStep_HasNothingToStop()
    {
        var steps = new DictationTargetSteps();

        Assert.Null(steps.Next(DictationPhase.Idle, recording: false, delivered: false));
        // The plugin accepted while the start was still running: the recording is the first step.
        Assert.Equal(DictationTargetPhase.Recording, steps.Next(DictationPhase.Recording, recording: true, delivered: false));
    }

    [Fact]
    public void Shown_FollowsThePluginsAnswer_AndExpires()
    {
        var time = new ManualTime();
        var steps = new DictationTargetSteps(time);

        steps.Reported(DictationTargetPhase.Recording, null);
        Assert.False(steps.Shown);
        Assert.Null(steps.ExpiresAt);

        steps.Reported(DictationTargetPhase.Transcribing, time.Now.AddSeconds(60));
        Assert.True(steps.Shown);
        Assert.Equal(time.Now.AddSeconds(60), steps.ExpiresAt);
        time.Now = time.Now.AddSeconds(61);
        Assert.False(steps.Shown);
        Assert.Null(steps.ExpiresAt);

        // A delivered dictation the app keeps showing needs no expiry.
        steps.Reported(DictationTargetPhase.Delivered, DateTimeOffset.MaxValue);
        Assert.True(steps.Shown);
        Assert.Null(steps.ExpiresAt);
    }
}
