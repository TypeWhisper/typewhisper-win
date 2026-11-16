namespace TypeWhisper.ModuleKit;

/// <summary>What the host offers a module.</summary>
/// <remarks>
/// Members are used on the UI thread, and the host calls back on the UI thread. A capability is null when the host
/// does not provide it, for example in a test host, so modules check before use.
/// </remarks>
public interface IModuleHost
{
    /// <summary>The host's informational version, such as <c>"1.3.0"</c>.</summary>
    string HostVersion { get; }

    /// <summary>
    /// The code of the interface language, such as <c>"en"</c>, <c>"de"</c>, <c>"ja"</c> or <c>"zh-Hans"</c>. It does not
    /// change while the app runs. See <see cref="ModuleTranslations"/>.
    /// </summary>
    string InterfaceLanguage { get; }

    /// <summary>Reads History records and their changes.</summary>
    IHistoryReading? HistoryReading { get; }

    /// <summary>Reads the managed policy keys that modules declare in <see cref="ModuleContributions.ManagedPolicyKeys"/>.</summary>
    IManagedPolicyReading? ManagedPolicyReading { get; }

    /// <summary>Reports the user's Premium access and its changes.</summary>
    IPremiumAccessReading? PremiumAccessReading { get; }

    /// <summary>Keeps module data with the user's data, so it is exported and erased with it.</summary>
    IUserDataLifecycle? UserDataLifecycle { get; }
}
