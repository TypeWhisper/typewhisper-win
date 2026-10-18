using TypeWhisper.Core.Models;
using Xunit;
using static TypeWhisper.Presentation.Tests.ManagedPolicySnapshots;

namespace TypeWhisper.Presentation.Tests;

public sealed class StartupPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "startup-policy-tests-" + Guid.NewGuid().ToString("N"));

    private StartupPolicyStore Store() => new(Path.Combine(_root, "startup-policy.json"));

    [Fact]
    public async Task EnforcedValueAppliesAndRetainsThePreviousRegistration()
    {
        var registration = new Registration { Enabled = false };
        var reconciler = new StartupPolicyReconciler(registration, Store());

        var outcome = await reconciler.ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]), setupCompleted: true);

        Assert.True(registration.Enabled);
        Assert.True(outcome.State.IsEnabled);
        Assert.True(outcome.Locked);
        Assert.Equal("Managed by your organization", outcome.LockReason);
        Assert.False(Store().Current.RetainedBeforeEnforcement!.Value);
        Assert.Equal(1, registration.Writes);

        await reconciler.ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]), setupCompleted: true);
        Assert.Equal(1, registration.Writes);
    }

    [Fact]
    public async Task RemovingEnforcementRestoresTheRetainedRegistrationOnce()
    {
        var registration = new Registration { Enabled = true };
        var reconciler = new StartupPolicyReconciler(registration, Store());
        await reconciler.ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 0)]), setupCompleted: true);
        Assert.False(registration.Enabled);

        var restored = await reconciler.ApplyAsync(Build(), setupCompleted: true);

        Assert.True(registration.Enabled);
        Assert.False(restored.Locked);
        Assert.Null(Store().Current.RetainedBeforeEnforcement);

        registration.Enabled = false;
        await reconciler.ApplyAsync(Build(), setupCompleted: true);
        Assert.False(registration.Enabled);
    }

    [Fact]
    public async Task RecommendationAppliesOnlyToANewProfileWithoutAnExplicitChoice()
    {
        var snapshot = Build(recommended: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]);

        var completed = new Registration { Enabled = false };
        await new StartupPolicyReconciler(completed, Store()).ApplyAsync(snapshot, setupCompleted: true);
        Assert.False(completed.Enabled);

        var chosen = new Registration { Enabled = false };
        var chosenReconciler = new StartupPolicyReconciler(chosen, Store());
        chosenReconciler.RecordUserChoice(false);
        await chosenReconciler.ApplyAsync(snapshot, setupCompleted: false);
        Assert.False(chosen.Enabled);

        var fresh = new Registration { Enabled = false };
        var freshStore = new StartupPolicyStore(Path.Combine(_root, "fresh", "startup-policy.json"));
        var freshReconciler = new StartupPolicyReconciler(fresh, freshStore);
        var outcome = await freshReconciler.ApplyAsync(snapshot, setupCompleted: false);
        Assert.True(fresh.Enabled);
        Assert.False(outcome.Locked);
        Assert.True(freshStore.Current.RecommendationApplied);

        fresh.Enabled = false;
        await freshReconciler.ApplyAsync(snapshot, setupCompleted: false);
        Assert.False(fresh.Enabled);
    }

    [Fact]
    public async Task UnreadablePolicyLocksTheSettingWithoutChangingIt()
    {
        var registration = new Registration { Enabled = true };
        var store = Store();
        var reconciler = new StartupPolicyReconciler(registration, store);

        var outcome = await reconciler.ApplyAsync(Unreadable(), setupCompleted: true);

        Assert.True(registration.Enabled);
        Assert.Equal(0, registration.Writes);
        Assert.True(outcome.Locked);
        Assert.Contains("could not be read", outcome.LockReason);
        Assert.False(File.Exists(Path.Combine(_root, "startup-policy.json")));
    }

    [Fact]
    public async Task UnchangeableRegistrationIsReportedNotForced()
    {
        var registration = new Registration { Enabled = false, CanChange = false };
        var reconciler = new StartupPolicyReconciler(registration, Store());

        var outcome = await reconciler.ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]), setupCompleted: true);

        Assert.False(registration.Enabled);
        Assert.Equal(0, registration.Writes);
        Assert.True(outcome.Locked);
        Assert.False(outcome.State.CanChange);
    }

    [Fact]
    public void StoreSurvivesAnUnreadableFile()
    {
        var path = Path.Combine(_root, "startup-policy.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "{not json");

        var store = new StartupPolicyStore(path);

        Assert.NotNull(store.Error);
        Assert.Equal(new StartupPolicyPreferences(), store.Current);
        Assert.True(store.Save(new(UserChoice: true)));
        Assert.True(new StartupPolicyStore(path).Current.UserChoice);
    }

    [Fact]
    public async Task RetainedChoiceSurvivesARestoreThatCannotHappenYet()
    {
        var registration = new Registration { Enabled = true };
        var reconciler = new StartupPolicyReconciler(registration, Store());
        await reconciler.ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 0)]), setupCompleted: true);
        Assert.False(registration.Enabled);

        // Windows refuses the change for now: the retained choice stays for a later pass.
        registration.CanChange = false;
        var blocked = await reconciler.ApplyAsync(Build(), setupCompleted: true);
        Assert.False(blocked.Locked);
        Assert.False(registration.Enabled);
        Assert.True(Store().Current.RetainedBeforeEnforcement);

        registration.CanChange = true;
        await reconciler.ApplyAsync(Build(), setupCompleted: true);
        Assert.True(registration.Enabled);
        Assert.Null(Store().Current.RetainedBeforeEnforcement);
    }

    [Fact]
    public async Task EnforcementWaitsUntilThePreviousStateIsSaved()
    {
        // A directory in place of the state file makes every save fail.
        Directory.CreateDirectory(Path.Combine(_root, "startup-policy.json"));
        var registration = new Registration { Enabled = true };
        var reconciler = new StartupPolicyReconciler(registration, Store());

        var outcome = await reconciler.ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 0)]), setupCompleted: true);

        Assert.True(outcome.Locked);
        Assert.True(registration.Enabled);
        Assert.Equal(0, registration.Writes);
        Assert.NotNull(reconciler.Store.Error);
    }

    [Fact]
    public async Task ARecommendationReplacingEnforcementAppliesInTheSamePass()
    {
        var registration = new Registration { Enabled = false };
        var reconciler = new StartupPolicyReconciler(registration, Store());
        await reconciler.ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]), setupCompleted: false);
        Assert.True(registration.Enabled);

        var outcome = await reconciler.ApplyAsync(Build(recommended: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]), setupCompleted: false);

        // Restored to the user's pre-enforcement state, then the recommendation turned it on again.
        Assert.True(registration.Enabled);
        Assert.False(outcome.Locked);
        Assert.Null(Store().Current.RetainedBeforeEnforcement);
        Assert.True(Store().Current.RecommendationApplied);
    }

    [Fact]
    public async Task EnforcementRetriesRetentionAfterAFailedWrite()
    {
        var registration = new Registration { Enabled = true };
        var store = Store();
        var reconciler = new StartupPolicyReconciler(registration, store);
        var snapshot = Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 0)]);
        // The first pass cannot write: a directory sits where the file belongs.
        var blocker = Path.Combine(_root, "startup-policy.json");
        Directory.CreateDirectory(blocker);
        await reconciler.ApplyAsync(snapshot, setupCompleted: true);
        Assert.True(registration.Enabled);
        Assert.False(store.Unreadable);

        // Once the write succeeds the enforcement applies without a restart.
        Directory.Delete(blocker);
        var outcome = await reconciler.ApplyAsync(snapshot, setupCompleted: true);
        Assert.False(registration.Enabled);
        Assert.True(outcome.Locked);
        Assert.True(store.Current.RetainedBeforeEnforcement);
    }

    [Fact]
    public void AUserChoiceSupersedesARetainedValue()
    {
        var store = Store();
        Assert.True(store.Save(new(RetainedBeforeEnforcement: true)));
        var reconciler = new StartupPolicyReconciler(new Registration(), store);

        Assert.True(reconciler.RecordUserChoice(false));

        Assert.False(store.Current.UserChoice);
        Assert.Null(store.Current.RetainedBeforeEnforcement);
    }

    [Fact]
    public void FailedSaveLeavesTheStoredStateUntouched()
    {
        Directory.CreateDirectory(Path.Combine(_root, "startup-policy.json"));
        var store = Store();

        Assert.False(store.Save(new(UserChoice: true, RetainedBeforeEnforcement: false)));

        Assert.Equal(new StartupPolicyPreferences(), store.Current);
        Assert.NotNull(store.Error);
    }

    [Fact]
    public async Task RecommendationWaitsForAReadableStoreAndAKnownRegistration()
    {
        var snapshot = Build(recommended: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]);

        // An unreadable preference file may hold the user's choice: no recommendation.
        var path = Path.Combine(_root, "startup-policy.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "{not json");
        var unreadable = new Registration { Enabled = false };
        await new StartupPolicyReconciler(unreadable, new StartupPolicyStore(path)).ApplyAsync(snapshot, setupCompleted: false);
        Assert.False(unreadable.Enabled);
        Assert.Equal(0, unreadable.Writes);

        // Enforcement over an unreadable state file leaves the file alone and keeps the registration as it is.
        var enforcedOverUnreadable = new Registration { Enabled = true };
        var locked = await new StartupPolicyReconciler(enforcedOverUnreadable, new StartupPolicyStore(path))
            .ApplyAsync(Build(enforced: [Number(ManagedPolicyKeys.LaunchAtLogin, 0)]), setupCompleted: false);
        Assert.True(locked.Locked);
        Assert.True(enforcedOverUnreadable.Enabled);
        Assert.Equal("{not json", File.ReadAllText(path));

        // A registration that could not be read is retried, not treated as the user's decision.
        var unknown = new Registration { Enabled = false, Unknown = true };
        var unknownStore = new StartupPolicyStore(Path.Combine(_root, "unknown", "startup-policy.json"));
        var reconciler = new StartupPolicyReconciler(unknown, unknownStore);
        await reconciler.ApplyAsync(snapshot, setupCompleted: false);
        Assert.False(unknownStore.Current.RecommendationApplied);
        unknown.Unknown = false;
        await reconciler.ApplyAsync(snapshot, setupCompleted: false);
        Assert.True(unknown.Enabled);
        Assert.True(unknownStore.Current.RecommendationApplied);
    }

    [Fact]
    public async Task RecommendationIsFinalOnlyOnceItTookEffect()
    {
        var snapshot = Build(recommended: [Number(ManagedPolicyKeys.LaunchAtLogin, 1)]);

        // A write that fails leaves the recommendation pending for the next pass.
        var failing = new Registration { Enabled = false, FailWrites = true };
        var failingStore = Store();
        var reconciler = new StartupPolicyReconciler(failing, failingStore);
        await reconciler.ApplyAsync(snapshot, setupCompleted: false);
        Assert.False(failing.Enabled);
        Assert.False(failingStore.Current.RecommendationApplied);
        failing.FailWrites = false;
        await reconciler.ApplyAsync(snapshot, setupCompleted: false);
        Assert.True(failing.Enabled);
        Assert.True(failingStore.Current.RecommendationApplied);

        // Windows keeping the app from changing startup counts as the user's decision.
        var disabled = new Registration { Enabled = false, CanChange = false };
        var disabledStore = new StartupPolicyStore(Path.Combine(_root, "disabled", "startup-policy.json"));
        await new StartupPolicyReconciler(disabled, disabledStore).ApplyAsync(snapshot, setupCompleted: false);
        Assert.False(disabled.Enabled);
        Assert.True(disabledStore.Current.RecommendationApplied);
    }

    private sealed class Registration : IStartupRegistration
    {
        public bool Enabled { get; set; }
        public bool CanChange { get; set; } = true;
        public bool FailWrites { get; set; }
        public bool Unknown { get; set; }
        public int Writes { get; private set; }

        public Task<StartupRegistrationState> ReadAsync() =>
            Task.FromResult(Unknown ? new StartupRegistrationState(false, false, "unreadable", Unknown: true) : new StartupRegistrationState(Enabled, CanChange, null));

        public Task<StartupRegistrationState> SetEnabledAsync(bool enabled)
        {
            if (!Unknown && CanChange && !FailWrites && Enabled != enabled)
            {
                Enabled = enabled;
                Writes++;
            }
            return ReadAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
