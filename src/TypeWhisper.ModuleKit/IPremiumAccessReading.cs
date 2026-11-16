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
