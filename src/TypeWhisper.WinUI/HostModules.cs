using System.Reflection;
using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// Optional modules that official builds can include. Other builds link a stub with the same assembly name that
// contributes nothing. Loading never stops the app: a module that fails is reported and left out.
internal sealed partial class HostModules
{
    // Assembly metadata that tells a stub build apart from one with modules.
    internal const string BuildKindKey = "TypeWhisperModules";
    internal const string StubBuildKind = "stub";

    private HostModules(string? buildKind, IReadOnlyList<IHostModule> modules, ModuleContributions contributions)
    {
        BuildKind = buildKind;
        Modules = modules;
        Contributions = contributions;
    }

    // The metadata value of the linked modules assembly, or null if it has none.
    internal string? BuildKind { get; }
    internal bool IsStub => BuildKind == StubBuildKind;
    // Only the modules whose contributions were accepted.
    internal IReadOnlyList<IHostModule> Modules { get; }
    internal ModuleContributions Contributions { get; }

    internal static string? BuildKindOf(Assembly modulesAssembly) =>
        modulesAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == BuildKindKey)?.Value;

    internal static HostModules Load(
        IModuleHost host,
        Assembly modulesAssembly,
        Func<IReadOnlyList<IHostModule>> createModules,
        Action<string, Exception> reportFailure)
    {
        var buildKind = BuildKindOf(modulesAssembly);
        IReadOnlyList<IHostModule> created;
        try
        {
            created = createModules() ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reportFailure("modules.create.failed", ex);
            return new(buildKind, [], new());
        }

        var accepted = new List<IHostModule>(created.Count);
        var combined = new ModuleContributions();
        foreach (var module in created.Where(module => module is not null))
        {
            // Each module fills its own collection, so one that throws halfway leaves nothing behind.
            var own = new ModuleContributions();
            try
            {
                module.Contribute(host, own);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reportFailure("modules.contribute.failed", ex);
                continue;
            }
            accepted.Add(module);
            foreach (var key in own.ManagedPolicyKeys) combined.ManagedPolicyKeys.Add(key);
        }
        return new(buildKind, accepted, combined);
    }
}
