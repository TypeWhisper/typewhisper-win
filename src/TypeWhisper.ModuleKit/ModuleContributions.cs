namespace TypeWhisper.ModuleKit;

/// <summary>
/// What modules add to the host. Each module fills its own instance in <see cref="IHostModule.Contribute"/>; the host
/// combines them once every module has contributed. More contribution points are added as the host offers them.
/// </summary>
public sealed class ModuleContributions
{
    /// <summary>The managed policy keys that modules read through <see cref="IManagedPolicyReading"/>.</summary>
    public IList<ModuleManagedPolicyKey> ManagedPolicyKeys { get; } = new List<ModuleManagedPolicyKey>();
}
