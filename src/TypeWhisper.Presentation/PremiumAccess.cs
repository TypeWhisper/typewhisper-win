namespace TypeWhisper.Presentation;

/// <summary>Verified access inputs. Supporter status alone does not grant paid features.</summary>
/// <param name="Commercial">A verified commercial license.</param>
/// <param name="PremiumAccount">A verified Premium entitlement.</param>
/// <param name="SignedIn">Whether an account session is available.</param>
/// <param name="Supporter">Supporter recognition without feature access.</param>
public sealed record PremiumAccess(bool Commercial = false, bool PremiumAccount = false,
    bool SignedIn = false, bool Supporter = false)
{
    /// <summary>Whether any paid access exists.</summary>
    public bool Any => Commercial || PremiumAccount;
    /// <summary>Returns the access step required by a feature.</summary>
    public PremiumRequirement Requirement(PremiumFeature feature) => feature switch
    {
        PremiumFeature.CalendarMeetings => Requirement(PremiumRequirement.CommercialOrPremium),
        PremiumFeature.CorrectionLearning => Requirement(PremiumRequirement.Commercial),
        PremiumFeature.CloudSync when !PremiumAccount => Requirement(PremiumRequirement.PremiumAccount),
        PremiumFeature.CloudSync => SignedIn ? PremiumRequirement.Available : PremiumRequirement.SignIn,
        _ => throw new ArgumentOutOfRangeException(nameof(feature))
    };

    /// <summary>
    /// Returns the access step for a feature that is not listed in <see cref="PremiumFeature"/>, such as one from an
    /// optional module, given the kind of access it needs.
    /// </summary>
    /// <param name="needed"><see cref="PremiumRequirement.CommercialOrPremium"/>, <see cref="PremiumRequirement.Commercial"/>
    /// or <see cref="PremiumRequirement.PremiumAccount"/>.</param>
    public PremiumRequirement Requirement(PremiumRequirement needed) => needed switch
    {
        PremiumRequirement.CommercialOrPremium => Any ? PremiumRequirement.Available : PremiumRequirement.CommercialOrPremium,
        PremiumRequirement.Commercial => Commercial ? PremiumRequirement.Available : PremiumRequirement.Commercial,
        // A signed-in commercial license holder gets Premium account access by linking the license.
        PremiumRequirement.PremiumAccount when !PremiumAccount => Commercial && SignedIn
            ? PremiumRequirement.LinkCommercialLicense : PremiumRequirement.PremiumAccount,
        PremiumRequirement.PremiumAccount => PremiumRequirement.Available,
        _ => throw new ArgumentOutOfRangeException(nameof(needed))
    };
}

/// <summary>Features with distinct entitlement requirements.</summary>
public enum PremiumFeature
{
    /// <summary>Calendar-driven recording.</summary>
    CalendarMeetings,
    /// <summary>Learning from edited dictation.</summary>
    CorrectionLearning,
    /// <summary>Cross-device cloud synchronization.</summary>
    CloudSync
}
/// <summary>Access requirements, independent of implementation availability.</summary>
public enum PremiumRequirement
{
    /// <summary>All access requirements are met.</summary>
    Available,
    /// <summary>Either commercial or Premium account access is needed.</summary>
    CommercialOrPremium,
    /// <summary>A commercial license is needed.</summary>
    Commercial,
    /// <summary>A Premium account entitlement is needed.</summary>
    PremiumAccount,
    /// <summary>An account session is needed.</summary>
    SignIn,
    /// <summary>The commercial license must be linked to the account.</summary>
    LinkCommercialLicense
}
