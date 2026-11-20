namespace TypeWhisper.ModuleKit;

/// <summary>Reports the user's Premium access and its changes.</summary>
/// <remarks>
/// This is what the host shows. A module that gates a feature also checks the signed entitlement itself, because a
/// check only in the host is easy to bypass.
/// </remarks>
public interface IPremiumAccessReading
{
    /// <summary>True with a commercial license or a Premium account.</summary>
    bool HasPremiumAccess { get; }

    /// <summary>Whether the current access meets <paramref name="requirement"/>.</summary>
    bool Meets(ModulePremiumRequirement requirement);

    /// <summary>Calls <paramref name="handler"/> on the UI thread whenever the access changes, until the result is disposed.</summary>
    IDisposable ObserveChanges(Action handler);

    /// <summary>Opens the settings where the user gets or manages Premium access.</summary>
    void ShowPremiumAccess();

    /// <summary>
    /// The Premium account entitlement the host verified, as the server signed it: the claims as JSON and their P-256
    /// signature (IEEE P1363, SHA-256), both base64url without padding, joined by a dot. Null while no account is
    /// signed in or it holds no entitlement. <see cref="ObserveChanges"/> reports when it changes. Since ModuleKit 1.2.
    /// </summary>
    /// <remarks>
    /// The claims are those of the host's account client: <c>status</c>, <c>tier</c>, <c>source</c>,
    /// <c>isLifetime</c>, <c>expiresAt</c>, <c>deviceLimit</c> and <c>verifiedAt</c>. A module verifies the signature
    /// with the key it trusts and reads the claims from what was signed.
    /// </remarks>
    string? SignedAccountEntitlement => null;
}

/// <summary>The kind of access a module feature needs.</summary>
public enum ModulePremiumRequirement
{
    /// <summary>A commercial license or a Premium account.</summary>
    CommercialOrPremium,
    /// <summary>A commercial license.</summary>
    Commercial,
    /// <summary>A Premium account.</summary>
    PremiumAccount
}
