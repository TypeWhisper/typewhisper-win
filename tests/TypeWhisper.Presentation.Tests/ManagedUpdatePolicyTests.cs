using TypeWhisper.Core.Models;
using Xunit;
using static TypeWhisper.Presentation.Tests.ManagedPolicySnapshots;

namespace TypeWhisper.Presentation.Tests;

public sealed class ManagedUpdatePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "update-policy-tests-" + Guid.NewGuid().ToString("N"));

    private AppUpdatePreferences Preferences(string version = "1.2.0") => new(Path.Combine(_root, "updates.json"), version);

    private static AppUpdateController Controller(AppUpdatePreferences preferences, Backend backend, Func<ManagedUpdatePolicy> policy) =>
        new(preferences, backend, apply => { apply(); return Task.FromResult<string?>(null); }, policy);

    [Fact]
    public void From_MapsModeChannelsAndAllowlist()
    {
        var snapshot = Build(
            enforced: [Number(ManagedPolicyKeys.UpdateMode, 1), Number(ManagedPolicyKeys.UpdateChannel, 2), Number(ManagedPolicyKeys.AllowedUpdateChannels + "Configured", 1)],
            recommended: [Number(ManagedPolicyKeys.UpdateChannel, 1)],
            enforcedLists: [List(ManagedPolicyKeys.AllowedUpdateChannels, "stable", " Release-Candidate ", "bogus")]);

        var policy = ManagedUpdatePolicy.From(snapshot);

        Assert.Equal(AppUpdateMode.CheckOnly, policy.Mode);
        Assert.Equal(AppUpdateChannel.ReleaseCandidate, policy.EnforcedChannel);
        Assert.Equal(AppUpdateChannel.Daily, policy.RecommendedChannel);
        Assert.Equal([AppUpdateChannel.Stable, AppUpdateChannel.ReleaseCandidate], policy.AllowedChannels!.Order());
        Assert.False(policy.Blocked);
        Assert.True(policy.IsManaged);
        Assert.True(policy.ChannelLocked);
    }

    [Fact]
    public void From_WithoutPolicyIsUnmanagedAndUnreadableIsBlocked()
    {
        Assert.False(ManagedUpdatePolicy.From(Build()).IsManaged);
        Assert.Equal(ManagedUpdatePolicy.None, ManagedUpdatePolicy.From(Build()));
        var blocked = ManagedUpdatePolicy.From(Unreadable());
        Assert.True(blocked.Blocked);
        Assert.True(blocked.ChannelLocked);
        Assert.Contains("could not be read", blocked.CheckRefusal(AppUpdateChannel.Stable));
        var invalid = ManagedUpdatePolicy.From(Build(enforced: [Number(ManagedPolicyKeys.UpdateMode, 7)]));
        Assert.True(invalid.Blocked);
    }

    [Fact]
    public void From_MachineWideInstallationIsExternalUnlessPolicySetsAMode()
    {
        var machineWide = ManagedUpdatePolicy.From(Build(), machineWide: true);
        Assert.Equal(AppUpdateMode.External, machineWide.Mode);
        Assert.True(machineWide.IsManaged);
        Assert.Contains("deployment", machineWide.CheckRefusal(AppUpdateChannel.Stable));

        var policyWins = ManagedUpdatePolicy.From(Build(enforced: [Number(ManagedPolicyKeys.UpdateMode, 2)]), machineWide: true);
        Assert.Equal(AppUpdateMode.UserInitiated, policyWins.Mode);
        Assert.Null(policyWins.InstallRefusal(AppUpdateChannel.Stable));
    }

    [Theory]
    [InlineData(@"C:\Program Files\TypeWhisper\", @"C:\Program Files\TypeWhisper", true)]
    [InlineData(@"c:\program files\typewhisper", @"C:\Program Files\TypeWhisper\", true)]
    [InlineData(@"C:\Program Files\TypeWhisper", @"C:\Users\a\AppData\Local\TypeWhisper", false)]
    [InlineData(null, @"C:\Program Files\TypeWhisper", false)]
    [InlineData(@"C:\Program Files\TypeWhisper", null, false)]
    [InlineData(" ", @"C:\Program Files\TypeWhisper", false)]
    public void MachineWideInstallation_MatchesOnlyItsOwnRoot(string? registered, string? root, bool expected) =>
        Assert.Equal(expected, MachineWideInstallation.Matches(registered, root));

    [Fact]
    public void Resolve_FollowsPrecedence()
    {
        var enforced = new ManagedUpdatePolicy(null, AppUpdateChannel.Daily, AppUpdateChannel.ReleaseCandidate, null, false);
        var recommended = enforced with { EnforcedChannel = null };

        Assert.Equal(AppUpdateChannel.Daily, enforced.Resolve(AppUpdateChannel.Stable, AppUpdateChannel.Stable));
        Assert.Equal(AppUpdateChannel.Stable, recommended.Resolve(AppUpdateChannel.Stable, AppUpdateChannel.Daily));
        Assert.Equal(AppUpdateChannel.ReleaseCandidate, recommended.Resolve(null, AppUpdateChannel.Stable));
        Assert.Equal(AppUpdateChannel.Stable, ManagedUpdatePolicy.None.Resolve(null, AppUpdateChannel.Stable));
    }

    [Fact]
    public void Refusals_FollowModeAndAllowlist()
    {
        var external = new ManagedUpdatePolicy(AppUpdateMode.External, null, null, null, false);
        var checkOnly = external with { Mode = AppUpdateMode.CheckOnly };
        var userInitiated = external with { Mode = AppUpdateMode.UserInitiated };
        var limited = new ManagedUpdatePolicy(null, null, null, new HashSet<AppUpdateChannel> { AppUpdateChannel.Stable }, false);
        var forcedOutside = limited with { EnforcedChannel = AppUpdateChannel.Daily };
        var none = limited with { AllowedChannels = new HashSet<AppUpdateChannel>() };

        Assert.Contains("deployment", external.CheckRefusal(AppUpdateChannel.Stable));
        Assert.Contains("deployment", external.InstallRefusal(AppUpdateChannel.Stable));
        Assert.Null(checkOnly.CheckRefusal(AppUpdateChannel.Stable));
        Assert.Contains("only checks", checkOnly.InstallRefusal(AppUpdateChannel.Stable));
        Assert.Null(userInitiated.CheckRefusal(AppUpdateChannel.Stable));
        Assert.Null(userInitiated.InstallRefusal(AppUpdateChannel.Stable));
        Assert.Null(limited.CheckRefusal(AppUpdateChannel.Stable));
        Assert.Contains("not allowed", limited.CheckRefusal(AppUpdateChannel.Daily));
        Assert.Contains("Contact your administrator", forcedOutside.CheckRefusal(AppUpdateChannel.Daily));
        Assert.Contains("turned off", none.CheckRefusal(AppUpdateChannel.Stable));
    }

    [Fact]
    public async Task ExternalModeRefusesChecksAndInstalls()
    {
        var backend = new Backend();
        var policy = new ManagedUpdatePolicy(AppUpdateMode.External, null, null, null, false);
        var controller = Controller(Preferences(), backend, () => policy);

        Assert.False(controller.CanCheck);
        Assert.False(controller.CanInstall);
        Assert.Contains("deployment", controller.Status);
        await controller.CheckAsync();
        await controller.InstallAsync();

        Assert.Empty(backend.Calls);
        Assert.Null(controller.Offer);
        Assert.Contains("deployment", controller.Status);
    }

    [Fact]
    public async Task CheckOnlyChecksButNeverInstalls()
    {
        var backend = new Backend();
        var policy = new ManagedUpdatePolicy(AppUpdateMode.CheckOnly, null, null, null, false);
        var controller = Controller(Preferences(), backend, () => policy);

        await controller.CheckAsync();

        Assert.Equal(["check"], backend.Calls);
        Assert.NotNull(controller.Offer);
        Assert.Contains("2.0.0", controller.Status);
        Assert.Contains("only checks", controller.Status);
        Assert.False(controller.CanInstall);

        await controller.InstallAsync();

        Assert.Equal(["check"], backend.Calls);
        Assert.Contains("only checks", controller.Status);
    }

    [Fact]
    public async Task CheckOnlyDoesNotPromiseAChannelSwitchForAnOlderVersion()
    {
        var backend = new Backend { Offer = new AppUpdateOffer("1.1.0", true) };
        var policy = new ManagedUpdatePolicy(AppUpdateMode.CheckOnly, null, null, null, false);
        var controller = Controller(Preferences(), backend, () => policy);

        await controller.CheckAsync();

        Assert.True(controller.Offer!.IsDowngrade);
        Assert.Contains("1.1.0", controller.Status);
        Assert.Contains("older than", controller.Status);
        Assert.Contains("only checks", controller.Status);
        Assert.DoesNotContain("switches", controller.Status);
        Assert.False(controller.CanInstall);
    }

    [Fact]
    public async Task EnforcedChannelIsUsedAndSelectionIsRefused()
    {
        var preferences = Preferences("1.2.0");
        Assert.True(preferences.Save(AppUpdateChannel.Stable));
        var backend = new Backend();
        var policy = new ManagedUpdatePolicy(null, AppUpdateChannel.Daily, null, null, false);
        var controller = Controller(preferences, backend, () => policy);

        Assert.Equal(AppUpdateChannel.Daily, controller.EffectiveChannel);
        Assert.True(controller.ChannelLocked);
        Assert.True(controller.AllowsDowngrade);
        await controller.CheckAsync();
        Assert.Equal(AppUpdateChannel.Daily, backend.CheckedChannel);
        Assert.True(backend.AllowedDowngrade);

        Assert.False(controller.Select(AppUpdateChannel.ReleaseCandidate));
        Assert.Equal(AppUpdateChannel.Stable, preferences.Channel);
        Assert.Contains("managed by your organization", controller.Status);
    }

    [Fact]
    public async Task AllowlistLimitsSelectionAndBlocksChecksOutsideIt()
    {
        var preferences = Preferences("1.2.0-daily.20261014.1");
        var backend = new Backend();
        var policy = new ManagedUpdatePolicy(null, null, null, new HashSet<AppUpdateChannel> { AppUpdateChannel.Stable }, false);
        var controller = Controller(preferences, backend, () => policy);

        Assert.Equal(AppUpdateChannel.Daily, controller.EffectiveChannel);
        Assert.False(controller.CanCheck);
        await controller.CheckAsync();
        Assert.Empty(backend.Calls);
        Assert.Contains("not allowed", controller.Status);

        Assert.False(controller.Select(AppUpdateChannel.ReleaseCandidate));
        Assert.True(controller.Select(AppUpdateChannel.Stable));
        Assert.True(controller.CanCheck);
        await controller.CheckAsync();
        Assert.Equal(AppUpdateChannel.Stable, backend.CheckedChannel);
    }

    [Fact]
    public async Task PolicyChangeDropsTheOfferAndTighteningDuringDownloadPreventsApply()
    {
        var backend = new Backend();
        var policy = ManagedUpdatePolicy.None;
        var controller = Controller(Preferences(), backend, () => policy);
        await controller.CheckAsync();
        Assert.NotNull(controller.Offer);

        // A changed update policy drops the cached offer even when it still allows the channel.
        policy = new ManagedUpdatePolicy(null, null, null, new HashSet<AppUpdateChannel> { AppUpdateChannel.Stable, AppUpdateChannel.Daily, AppUpdateChannel.ReleaseCandidate }, false);
        controller.PolicyChanged();
        Assert.Null(controller.Offer);

        await controller.CheckAsync();
        backend.OnDownload = () => policy = new ManagedUpdatePolicy(AppUpdateMode.CheckOnly, null, null, null, false);
        await controller.InstallAsync();

        Assert.Equal(["check", "check", "download"], backend.Calls);
        Assert.Null(controller.Offer);
        Assert.Contains("only checks", controller.Status);
    }

    [Fact]
    public async Task ChannelChangedByPolicyAfterTheCheckRequiresANewCheck()
    {
        var backend = new Backend();
        var policy = ManagedUpdatePolicy.None;
        var controller = Controller(Preferences("1.2.0"), backend, () => policy);
        await controller.CheckAsync();
        Assert.NotNull(controller.Offer);

        policy = new ManagedUpdatePolicy(null, AppUpdateChannel.Daily, null, null, false);
        await controller.InstallAsync();

        Assert.Equal(["check"], backend.Calls);
        Assert.Contains("Check for updates again", controller.Status);
    }

    [Fact]
    public void RecommendationAppliesUntilTheUserChoosesExplicitly()
    {
        var preferences = Preferences("1.2.0");
        var policy = new ManagedUpdatePolicy(null, null, AppUpdateChannel.Daily, null, false);
        var controller = Controller(preferences, new Backend(), () => policy);

        Assert.False(preferences.HasExplicitChoice);
        Assert.Equal(AppUpdateChannel.Daily, controller.EffectiveChannel);
        Assert.False(controller.ChannelLocked);

        Assert.True(controller.Select(AppUpdateChannel.Stable));

        Assert.True(preferences.HasExplicitChoice);
        Assert.Equal(AppUpdateChannel.Stable, controller.EffectiveChannel);
        Assert.Equal(AppUpdateChannel.Stable, Preferences("1.2.0").ExplicitChannel);
    }

    [Fact]
    public void AnUnreadableSavedChannelIsNotReplacedByARecommendation()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "updates.json"), "not json");
        var preferences = Preferences();
        Assert.NotNull(preferences.Error);
        Assert.False(preferences.HasExplicitChoice);
        Assert.True(preferences.ChoiceUnknown);
        var policy = new ManagedUpdatePolicy(null, null, AppUpdateChannel.Daily, null, false);
        var controller = Controller(preferences, new Backend(), () => policy);

        Assert.Equal(preferences.InstalledChannel, controller.EffectiveChannel);

        // Enforcement still wins over the unknown choice.
        policy = new ManagedUpdatePolicy(null, AppUpdateChannel.Daily, null, null, false);
        Assert.Equal(AppUpdateChannel.Daily, controller.EffectiveChannel);
    }

    [Fact]
    public async Task PolicyChangedKeepsTheOfferWhenTheUpdatePolicyIsUnchanged()
    {
        var policy = new ManagedUpdatePolicy(null, null, null, new HashSet<AppUpdateChannel> { AppUpdateChannel.Stable }, false);
        var backend = new Backend();
        var controller = Controller(Preferences(), backend, () => policy);
        await controller.CheckAsync();
        Assert.NotNull(controller.Offer);

        // Another key changed; the update projection is equal by content.
        policy = new ManagedUpdatePolicy(null, null, null, new HashSet<AppUpdateChannel> { AppUpdateChannel.Stable }, false);
        controller.PolicyChanged();
        Assert.NotNull(controller.Offer);
        Assert.True(controller.CanInstall);

        policy = new ManagedUpdatePolicy(AppUpdateMode.CheckOnly, null, null, new HashSet<AppUpdateChannel> { AppUpdateChannel.Stable }, false);
        controller.PolicyChanged();
        Assert.Null(controller.Offer);
    }

    [Fact]
    public async Task CheckDiscardsTheResultWhenPolicyChangesDuringTheCheck()
    {
        var policy = ManagedUpdatePolicy.None;
        var backend = new Backend();
        var controller = Controller(Preferences(), backend, () => policy);
        backend.OnCheck = () => policy = new ManagedUpdatePolicy(AppUpdateMode.External, null, null, null, false);

        await controller.CheckAsync();

        Assert.Equal(["check"], backend.Calls);
        Assert.Null(controller.Offer);
        Assert.False(controller.CanInstall);
        Assert.DoesNotContain("available", controller.Status);
    }

    [Fact]
    public async Task ApplyRereadsPolicyAtTheApplyPoint()
    {
        var policy = ManagedUpdatePolicy.None;
        var backend = new Backend();
        // The host drains work before it runs the apply action; policy becomes check-only meanwhile.
        var controller = new AppUpdateController(Preferences(), backend, apply =>
        {
            policy = new ManagedUpdatePolicy(AppUpdateMode.CheckOnly, null, null, null, false);
            apply();
            return Task.FromResult<string?>(null);
        }, () => policy);
        await controller.CheckAsync();
        Assert.True(controller.CanInstall);

        await controller.InstallAsync();

        Assert.Equal(["check", "download"], backend.Calls);
        Assert.Contains("only checks", controller.Status);
        Assert.Null(controller.Offer);
    }

    private sealed class Backend : IAppUpdateBackend
    {
        public List<string> Calls { get; } = [];
        public AppUpdateChannel? CheckedChannel { get; private set; }
        public bool AllowedDowngrade { get; private set; }
        public Action? OnDownload { get; set; }
        public Action? OnCheck { get; set; }
        public AppUpdateOffer Offer { get; init; } = new("2.0.0", false);
        public string? UnavailableReason => null;

        public Task<AppUpdateCheck> CheckAsync(AppUpdateChannel channel, bool allowDowngrade)
        {
            Calls.Add("check");
            CheckedChannel = channel;
            AllowedDowngrade = allowDowngrade;
            OnCheck?.Invoke();
            return Task.FromResult(new AppUpdateCheck(true, Offer));
        }

        public Task DownloadAsync(AppUpdateOffer offer)
        {
            Calls.Add("download");
            OnDownload?.Invoke();
            return Task.CompletedTask;
        }

        public void Apply(AppUpdateOffer offer) => Calls.Add("apply");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
