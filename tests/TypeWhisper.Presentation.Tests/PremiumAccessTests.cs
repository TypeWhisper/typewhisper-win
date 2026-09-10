using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class PremiumAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premium-tests-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(_root, "premium-development.txt");

    [Fact]
    public void VerifiedLicenseChangesNotifyConsumersWithoutWritingDevelopmentState()
    {
        var actual = new PremiumAccess();
        var state = new PremiumAccessState(Store, () => actual);
        var changes = 0; state.Changed += () => changes++;
        actual = new PremiumAccess(Commercial: true);
        state.NotifyActualAccessChanged();
        Assert.True(state.Current.Commercial);
        actual = new PremiumAccess(Supporter: true);
        state.NotifyActualAccessChanged();
        Assert.False(state.Current.Commercial);
        Assert.True(state.Current.Supporter);
        Assert.Equal(2, changes);
        Assert.False(File.Exists(Store));
    }

    [Fact]
    public void AccessMatrixMatchesFeatureRequirements()
    {
        foreach (var commercial in new[] { false, true })
        foreach (var premium in new[] { false, true })
        foreach (var signedIn in new[] { false, true })
        foreach (var supporter in new[] { false, true })
        {
            var access = new PremiumAccess(commercial, premium, signedIn, supporter);
            Assert.Equal(commercial || premium, access.Requirement(PremiumFeature.CalendarMeetings) == PremiumRequirement.Available);
            Assert.Equal(commercial, access.Requirement(PremiumFeature.CorrectionLearning) == PremiumRequirement.Available);
            Assert.Equal(premium && signedIn, access.Requirement(PremiumFeature.CloudSync) == PremiumRequirement.Available);
        }
        Assert.Equal(PremiumRequirement.LinkCommercialLicense, new PremiumAccess(Commercial: true, SignedIn: true).Requirement(PremiumFeature.CloudSync));
        Assert.Equal(PremiumRequirement.SignIn, new PremiumAccess(PremiumAccount: true).Requirement(PremiumFeature.CloudSync));
    }

    [Fact]
    public void ResetRestoresActualAccessAndDeletesOnlyDevelopmentState()
    {
        Directory.CreateDirectory(_root);
        var actual = new PremiumAccess(Supporter: true);
        var state = new PremiumAccessState(Store, () => actual);
        Assert.Equal(actual, state.Current);
#if DEBUG
        Assert.True(state.SetScenario(PremiumDevScenario.All));
        Assert.True(new PremiumAccessState(Store, () => actual).Current.Commercial);
        Assert.True(state.SetScenario(PremiumDevScenario.Actual));
        Assert.False(File.Exists(Store));
#else
        File.WriteAllText(Store, "All");
        state = new PremiumAccessState(Store, () => actual);
        Assert.False(state.SetScenario(PremiumDevScenario.All));
        Assert.Equal("All", File.ReadAllText(Store));
#endif
        Assert.False(state.IsOverridden);
        Assert.Equal(actual, state.Current);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("999")]
    [InlineData("")]
    public void InvalidStoredAccessNeverGrantsFeatures(string value)
    {
        Directory.CreateDirectory(_root); File.WriteAllText(Store, value);
        var state = new PremiumAccessState(Store, () => new());
        Assert.False(state.Current.Any);
        Assert.False(state.IsOverridden);
        Assert.Equal(value, File.ReadAllText(Store));
    }

    [Fact]
    public void FailedSaveKeepsPreviousAccess()
    {
        Directory.CreateDirectory(Store);
        var state = new PremiumAccessState(Store, () => new());
        Assert.False(state.SetScenario(PremiumDevScenario.All));
        Assert.False(state.Current.Any);
#if DEBUG
        Assert.NotNull(state.Error);
#endif
    }

    [Fact]
    public void AccessChangesArePublishedAndPersistAcrossRecreation()
    {
        var state = new PremiumAccessState(Store, () => new());
        var changes = 0; state.Changed += () => changes++;
#if DEBUG
        foreach (var scenario in Enum.GetValues<PremiumDevScenario>().Where(s => s != PremiumDevScenario.Actual))
        {
            Assert.True(state.SetScenario(scenario));
            var reloaded = new PremiumAccessState(Store, () => new());
            Assert.Equal(scenario, reloaded.Scenario);
            Assert.Equal(state.Current, reloaded.Current);
        }
        Assert.Equal(6, changes);
#else
        Assert.False(PremiumAccessState.CanOverride);
        Assert.False(state.SetScenario(PremiumDevScenario.Premium));
        Assert.Equal(0, changes);
#endif
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
