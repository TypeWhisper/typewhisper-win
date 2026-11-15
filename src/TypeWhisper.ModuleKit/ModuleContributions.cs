namespace TypeWhisper.ModuleKit;

/// <summary>
/// What modules add to the host. Each module fills its own instance in <see cref="IHostModule.Contribute"/>; the host
/// combines them once every module has contributed. More contribution points are added as the host offers them.
/// </summary>
public sealed class ModuleContributions
{
    /// <summary>Names of the managed policy values (MDM) that a module reads.</summary>
    public IList<string> ManagedPolicyKeys { get; } = new List<string>();
}
