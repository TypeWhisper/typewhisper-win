using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Reads <c>SkipOnboardingWhenReady</c> and <c>SuppressOptionalPrompts</c> from a policy snapshot.</summary>
public static class OnboardingPolicy
{
    /// <summary>Whether policy asks to keep the initial setup closed once its prerequisites are met. Enforced only.</summary>
    public static bool SkipSetupWhenReady(ManagedPolicySnapshot snapshot) =>
        !snapshot.IsBlocked(ManagedPolicyKeys.SkipOnboardingWhenReady)
        && snapshot.Enforced(ManagedPolicyKeys.SkipOnboardingWhenReady)?.IsTrue == true;

    /// <summary>
    /// Whether optional welcome and promotional prompts stay hidden. Required setup and errors are never affected. An
    /// unreadable policy keeps the last known value and an invalid enforced value keeps prompts hidden: suppression
    /// ends only when a readable snapshot removes or disables it.
    /// </summary>
    public static bool SuppressOptionalPrompts(ManagedPolicySnapshot snapshot) =>
        snapshot.InvalidEnforcedKeys.Contains(ManagedPolicyKeys.SuppressOptionalPrompts)
        || snapshot.ResolveBoolean(ManagedPolicyKeys.SuppressOptionalPrompts, false).Value;

    /// <summary>Whether an unfinished setup may stay closed: policy asks for it and every prerequisite is actually met.</summary>
    public static bool CanSkipSetup(ManagedPolicySnapshot snapshot, bool hasShortcut, bool modelReady, bool microphoneAvailable) =>
        SkipSetupWhenReady(snapshot) && SetupReadiness.Validate(false, hasShortcut, modelReady, microphoneAvailable) is null;
}
