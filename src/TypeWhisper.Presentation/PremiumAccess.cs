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
        PremiumFeature.CalendarMeetings => Any ? PremiumRequirement.Available : PremiumRequirement.CommercialOrPremium,
        PremiumFeature.CorrectionLearning => Commercial ? PremiumRequirement.Available : PremiumRequirement.Commercial,
        PremiumFeature.CloudSync when !PremiumAccount => Commercial && SignedIn
            ? PremiumRequirement.LinkCommercialLicense : PremiumRequirement.PremiumAccount,
        PremiumFeature.CloudSync => SignedIn ? PremiumRequirement.Available : PremiumRequirement.SignIn,
        _ => throw new ArgumentOutOfRangeException(nameof(feature))
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
