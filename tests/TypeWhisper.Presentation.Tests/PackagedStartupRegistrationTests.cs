using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class PackagedStartupRegistrationTests
{
    [Theory]
    [InlineData(PackagedStartupState.DisabledByUser, false)]
    [InlineData(PackagedStartupState.DisabledByPolicy, false)]
    [InlineData(PackagedStartupState.EnabledByPolicy, true)]
    public async Task WindowsChoicesAreReportedWithoutAttemptingChanges(PackagedStartupState state, bool enabled)
    {
        var changes = 0;
        var registration = new PackagedStartupRegistration(() => Task.FromResult(state),
            () => { changes++; return Task.CompletedTask; }, () => { changes++; return Task.CompletedTask; });
        var result = await registration.SetEnabledAsync(!enabled);
        Assert.Equal(enabled, result.IsEnabled);
        Assert.False(result.CanChange);
        Assert.NotNull(result.Error);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ReadsBackTaskAfterEnablingAndDisabling()
    {
        var state = PackagedStartupState.Disabled;
        var registration = new PackagedStartupRegistration(() => Task.FromResult(state),
            () => { state = PackagedStartupState.Enabled; return Task.CompletedTask; },
            () => { state = PackagedStartupState.Disabled; return Task.CompletedTask; });
        Assert.True((await registration.SetEnabledAsync(true)).IsEnabled);
        var disabled = await registration.SetEnabledAsync(false);
        Assert.False(disabled.IsEnabled);
        Assert.True(disabled.CanChange);
        Assert.Null(disabled.Error);
    }

    [Fact]
    public async Task DeniedEnableDoesNotClaimSuccess()
    {
        var state = PackagedStartupState.Disabled;
        var registration = new PackagedStartupRegistration(() => Task.FromResult(state),
            () => { state = PackagedStartupState.DisabledByUser; return Task.CompletedTask; },
            () => Task.CompletedTask);
        var result = await registration.SetEnabledAsync(true);
        Assert.False(result.IsEnabled);
        Assert.False(result.CanChange);
        Assert.Contains("Windows", result.Error);
    }

    [Fact]
    public async Task MissingPackageIdentityIsVisibleAndCannotChangeStartup()
    {
        var registration = new PackagedStartupRegistration(
            () => Task.FromException<PackagedStartupState>(new InvalidOperationException("No package identity")),
            () => throw new InvalidOperationException("Must not enable"),
            () => throw new InvalidOperationException("Must not disable"));
        var result = await registration.SetEnabledAsync(true);
        Assert.False(result.CanChange);
        Assert.Contains("No package identity", result.Error);
    }
}
