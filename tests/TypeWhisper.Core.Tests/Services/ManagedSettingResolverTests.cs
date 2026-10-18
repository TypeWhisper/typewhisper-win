using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class ManagedSettingResolverTests
{
    [Theory]
    [InlineData(false, false, false, "stable", ManagedSettingSource.ApplicationDefault)]
    [InlineData(false, false, true, "enforced", ManagedSettingSource.EnforcedPolicy)]
    [InlineData(false, true, false, "recommended", ManagedSettingSource.RecommendedPolicy)]
    [InlineData(false, true, true, "enforced", ManagedSettingSource.EnforcedPolicy)]
    [InlineData(true, false, false, "user", ManagedSettingSource.UserPreference)]
    [InlineData(true, false, true, "enforced", ManagedSettingSource.EnforcedPolicy)]
    [InlineData(true, true, false, "user", ManagedSettingSource.UserPreference)]
    [InlineData(true, true, true, "enforced", ManagedSettingSource.EnforcedPolicy)]
    public void Resolve_AllSourceCombinations_UsesDeclaredPrecedence(
        bool hasUserPreference,
        bool hasRecommendation,
        bool hasEnforcedPolicy,
        string expectedValue,
        ManagedSettingSource expectedSource)
    {
        var result = ManagedSettingResolver.Resolve(
            applicationDefault: "stable",
            explicitUserPreference: hasUserPreference ? new("user") : null,
            recommendedPolicy: hasRecommendation ? new("recommended") : null,
            enforcedPolicy: hasEnforcedPolicy ? new("enforced") : null);

        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedSource, result.Source);
        Assert.Equal(hasEnforcedPolicy, result.IsEnforced);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_ExplicitFactoryDefault_WinsOverRecommendation(bool factoryDefault)
    {
        var result = ManagedSettingResolver.Resolve(
            applicationDefault: factoryDefault,
            explicitUserPreference: new(factoryDefault),
            recommendedPolicy: new(!factoryDefault));

        Assert.Equal(factoryDefault, result.Value);
        Assert.Equal(ManagedSettingSource.UserPreference, result.Source);
        Assert.False(result.IsEnforced);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_EnforcedBoolean_IsPresentRegardlessOfItsValue(bool enforcedValue)
    {
        var result = ManagedSettingResolver.Resolve(
            applicationDefault: !enforcedValue,
            explicitUserPreference: new(!enforcedValue),
            recommendedPolicy: new(!enforcedValue),
            enforcedPolicy: new(enforcedValue));

        Assert.Equal(enforcedValue, result.Value);
        Assert.Equal(ManagedSettingSource.EnforcedPolicy, result.Source);
        Assert.True(result.IsEnforced);
    }

    [Fact]
    public void Resolve_RecommendedFalse_AppliesToANewProfileAndRemainsEditable()
    {
        var recommendation = new ManagedSettingValue<bool>(false);

        var initial = ManagedSettingResolver.Resolve(
            applicationDefault: true,
            recommendedPolicy: recommendation);
        var afterUserChoice = ManagedSettingResolver.Resolve(
            applicationDefault: true,
            explicitUserPreference: new(true),
            recommendedPolicy: recommendation);

        Assert.False(initial.Value);
        Assert.Equal(ManagedSettingSource.RecommendedPolicy, initial.Source);
        Assert.False(initial.IsEnforced);
        Assert.True(afterUserChoice.Value);
        Assert.Equal(ManagedSettingSource.UserPreference, afterUserChoice.Source);
        Assert.False(afterUserChoice.IsEnforced);
    }

    [Fact]
    public void Resolve_EnforcementAddedChangedAndRemoved_RevealsRetainedUserChoice()
    {
        var userPreference = new ManagedSettingValue<string>("daily");
        var recommendation = new ManagedSettingValue<string>("stable");

        var enforced = ManagedSettingResolver.Resolve(
            "stable", userPreference, recommendation, new("release-candidate"));
        var changed = ManagedSettingResolver.Resolve(
            "stable", userPreference, recommendation, new("stable"));
        var removed = ManagedSettingResolver.Resolve(
            "stable", userPreference, recommendation);

        Assert.Equal("release-candidate", enforced.Value);
        Assert.True(enforced.IsEnforced);
        Assert.Equal("stable", changed.Value);
        Assert.True(changed.IsEnforced);
        Assert.Equal("daily", userPreference.Value);
        Assert.Equal("daily", removed.Value);
        Assert.Equal(ManagedSettingSource.UserPreference, removed.Source);
        Assert.False(removed.IsEnforced);
    }

    [Fact]
    public void Resolve_RemovingPoliciesWithoutUserChoice_FallsBackThroughRecommendationToDefault()
    {
        var enforced = ManagedSettingResolver.Resolve(
            applicationDefault: "stable",
            recommendedPolicy: new("daily"),
            enforcedPolicy: new("release-candidate"));
        var recommended = ManagedSettingResolver.Resolve(
            applicationDefault: "stable",
            recommendedPolicy: new("daily"));
        var unmanaged = ManagedSettingResolver.Resolve(applicationDefault: "stable");

        Assert.Equal("release-candidate", enforced.Value);
        Assert.Equal(ManagedSettingSource.EnforcedPolicy, enforced.Source);
        Assert.Equal("daily", recommended.Value);
        Assert.Equal(ManagedSettingSource.RecommendedPolicy, recommended.Source);
        Assert.Equal("stable", unmanaged.Value);
        Assert.Equal(ManagedSettingSource.ApplicationDefault, unmanaged.Source);
    }

    [Fact]
    public void Resolve_DifferentUsers_KeepTheirOwnChoicesAcrossTheSameMachinePolicy()
    {
        var firstUser = new ManagedSettingValue<bool>(false);
        var secondUser = new ManagedSettingValue<bool>(true);
        var enforced = new ManagedSettingValue<bool>(true);

        Assert.True(ManagedSettingResolver.Resolve(false, firstUser, enforcedPolicy: enforced).Value);
        Assert.True(ManagedSettingResolver.Resolve(false, secondUser, enforcedPolicy: enforced).Value);

        Assert.False(ManagedSettingResolver.Resolve(false, firstUser).Value);
        Assert.True(ManagedSettingResolver.Resolve(false, secondUser).Value);
    }
}
