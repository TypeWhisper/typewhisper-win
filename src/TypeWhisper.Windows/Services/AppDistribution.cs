namespace TypeWhisper.Windows.Services;

/// <summary>
/// Lists the supported TypeWhisper Windows distribution channels.
/// </summary>
public enum AppDistributionKind
{
    /// <summary>
    /// Represents the direct GitHub, WinGet, or Velopack distribution.
    /// </summary>
    Direct,

    /// <summary>
    /// Represents the Microsoft Store MSIX distribution.
    /// </summary>
    Store
}

/// <summary>
/// Provides app distribution metadata.
/// </summary>
public static class AppDistribution
{
    /// <summary>
    /// Gets the current distribution kind.
    /// </summary>
    public static AppDistributionKind Current =>
#if TYPEWHISPER_STORE
        AppDistributionKind.Store;
#else
        AppDistributionKind.Direct;
#endif

    /// <summary>
    /// Gets the Microsoft Store package identity reserved in Partner Center.
    /// </summary>
    public static StorePackageIdentity StoreIdentity { get; } = new(
        "TypeWhisper.TypeWhisper",
        "CN=C90DFED3-0D3C-493E-8620-903C9B1A1D75",
        "TypeWhisper",
        "TypeWhisper.TypeWhisper_51tqb5623pxja",
        "9PF42ZCR0JR0",
        "ms-windows-store://pdp/?productid=9PF42ZCR0JR0");
}

/// <summary>
/// Represents Microsoft Store package identity values.
/// </summary>
public sealed class StorePackageIdentity
{
    /// <summary>
    /// Initializes a new instance of the StorePackageIdentity class.
    /// </summary>
    public StorePackageIdentity(
        string packageIdentityName,
        string packagePublisher,
        string publisherDisplayName,
        string packageFamilyName,
        string storeProductId,
        string storeProtocolLink)
    {
        PackageIdentityName = packageIdentityName;
        PackagePublisher = packagePublisher;
        PublisherDisplayName = publisherDisplayName;
        PackageFamilyName = packageFamilyName;
        StoreProductId = storeProductId;
        StoreProtocolLink = storeProtocolLink;
    }

    /// <summary>
    /// Gets the package identity name.
    /// </summary>
    public string PackageIdentityName { get; }

    /// <summary>
    /// Gets the package publisher distinguished name.
    /// </summary>
    public string PackagePublisher { get; }

    /// <summary>
    /// Gets the package publisher display name.
    /// </summary>
    public string PublisherDisplayName { get; }

    /// <summary>
    /// Gets the package family name.
    /// </summary>
    public string PackageFamilyName { get; }

    /// <summary>
    /// Gets the Microsoft Store product id.
    /// </summary>
    public string StoreProductId { get; }

    /// <summary>
    /// Gets the Microsoft Store protocol link.
    /// </summary>
    public string StoreProtocolLink { get; }
}
