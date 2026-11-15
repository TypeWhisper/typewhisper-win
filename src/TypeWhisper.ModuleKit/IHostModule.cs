namespace TypeWhisper.ModuleKit;

/// <summary>
/// An optional module that runs inside TypeWhisper. Official builds can include modules; other builds link a stub
/// that provides none.
/// </summary>
public interface IHostModule
{
    /// <summary>A stable identifier such as <c>"example"</c>. It must not change between versions.</summary>
    string Id { get; }

    /// <summary>
    /// Adds what the module offers to <paramref name="contributions"/>. The host calls this once at startup and binds
    /// the contributions at fixed points. A module that throws here is left out.
    /// </summary>
    /// <param name="host">The capabilities the host makes available to modules.</param>
    /// <param name="contributions">Collects the module's contributions.</param>
    void Contribute(IModuleHost host, ModuleContributions contributions);
}
