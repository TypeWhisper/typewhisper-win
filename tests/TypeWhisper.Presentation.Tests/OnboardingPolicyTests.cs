using TypeWhisper.Core.Models;
using Xunit;
using static TypeWhisper.Presentation.Tests.ManagedPolicySnapshots;

namespace TypeWhisper.Presentation.Tests;

public sealed class OnboardingPolicyTests
{
    [Fact]
    public void SkipSetup_IsEnforcedOnlyAndNeedsEveryPrerequisite()
    {
        var enforced = Build(enforced: [Number(ManagedPolicyKeys.SkipOnboardingWhenReady, 1)]);
        var recommendedOnly = Build(recommended: [Number(ManagedPolicyKeys.SkipOnboardingWhenReady, 1)]);
        var off = Build(enforced: [Number(ManagedPolicyKeys.SkipOnboardingWhenReady, 0)]);

        Assert.True(OnboardingPolicy.SkipSetupWhenReady(enforced));
        Assert.False(OnboardingPolicy.SkipSetupWhenReady(recommendedOnly));
        Assert.False(OnboardingPolicy.SkipSetupWhenReady(off));
        Assert.False(OnboardingPolicy.SkipSetupWhenReady(Unreadable()));

        Assert.True(OnboardingPolicy.CanSkipSetup(enforced, hasShortcut: true, modelReady: true, microphoneAvailable: true));
        Assert.False(OnboardingPolicy.CanSkipSetup(enforced, hasShortcut: false, modelReady: true, microphoneAvailable: true));
        Assert.False(OnboardingPolicy.CanSkipSetup(enforced, hasShortcut: true, modelReady: false, microphoneAvailable: true));
        Assert.False(OnboardingPolicy.CanSkipSetup(enforced, hasShortcut: true, modelReady: true, microphoneAvailable: false));
        Assert.False(OnboardingPolicy.CanSkipSetup(off, hasShortcut: true, modelReady: true, microphoneAvailable: true));
    }

    [Fact]
    public void SuppressOptionalPrompts_AcceptsEnforcementAndRecommendation()
    {
        Assert.True(OnboardingPolicy.SuppressOptionalPrompts(Build(enforced: [Number(ManagedPolicyKeys.SuppressOptionalPrompts, 1)])));
        Assert.True(OnboardingPolicy.SuppressOptionalPrompts(Build(recommended: [Number(ManagedPolicyKeys.SuppressOptionalPrompts, 1)])));
        Assert.False(OnboardingPolicy.SuppressOptionalPrompts(Build(enforced: [Number(ManagedPolicyKeys.SuppressOptionalPrompts, 0)])));
        Assert.False(OnboardingPolicy.SuppressOptionalPrompts(Build()));
        Assert.False(OnboardingPolicy.SuppressOptionalPrompts(Unreadable()));
    }
}
