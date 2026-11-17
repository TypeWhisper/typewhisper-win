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
    internal const string UnsupportedVersionStage = "modules.version.unsupported";

    private HostModules(string? buildKind, IReadOnlyList<IHostModule> modules, ModuleContributions contributions, ModuleContributionCatalog? catalog = null)
    {
        BuildKind = buildKind;
        Modules = modules;
        Contributions = contributions;
        Catalog = catalog ?? ModuleContributionCatalog.Empty;
    }

    // The metadata value of the linked modules assembly, or null if it has none.
    internal string? BuildKind { get; }
    internal bool IsStub => BuildKind == StubBuildKind;
    // Only the modules whose contributions were accepted.
    internal IReadOnlyList<IHostModule> Modules { get; }
    internal ModuleContributions Contributions { get; }
    // The combined contributions after validation, as the host binds them.
    internal ModuleContributionCatalog Catalog { get; }

    internal static string? BuildKindOf(Assembly modulesAssembly) =>
        modulesAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == BuildKindKey)?.Value;

    internal static HostModules Load(
        IModuleHost host,
        Assembly modulesAssembly,
        ModuleKitVersion requiredVersion,
        Func<IReadOnlyList<IHostModule>> createModules,
        Action<string, Exception> reportFailure,
        Action<string>? reportRejection = null,
        Func<string?, IModuleDiagnostics?>? diagnosticsFor = null)
    {
        var buildKind = BuildKindOf(modulesAssembly);
        // Modules written against an interface this host does not provide could fail anywhere, so none are created.
        // The log keeps only the stage and its data, not exception messages, so both versions go into the stage too.
        var supported = ModuleKitVersion.Current;
        if (!supported.Supports(requiredVersion))
        {
            reportFailure($"{UnsupportedVersionStage} required={requiredVersion} supported={supported}",
                new NotSupportedException($"The modules require ModuleKit {requiredVersion}, but this host provides {supported}."));
            return new(buildKind, [], new());
        }

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
                // With diagnostics, each module sees the host with its own, named after the module's identifier.
                module.Contribute(diagnosticsFor is null ? host : new ModuleScopedHost(host, diagnosticsFor(module.Id)), own);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reportFailure("modules.contribute.failed", ex);
                continue;
            }
            accepted.Add(module);
            Append(own.ManagedPolicyKeys, combined.ManagedPolicyKeys);
            Append(own.HotkeyActions, combined.HotkeyActions);
            Append(own.MenuItems, combined.MenuItems);
            Append(own.Panels, combined.Panels);
            Append(own.RecordActions, combined.RecordActions);
            Append(own.PremiumCards, combined.PremiumCards);
            Append(own.SettingsSections, combined.SettingsSections);
            Append(own.FileTranscriptionOptions, combined.FileTranscriptionOptions);
            Append(own.FileTranscriptionCompleted, combined.FileTranscriptionCompleted);
            Append(own.HistorySidebarGroups, combined.HistorySidebarGroups);
        }
        return new(buildKind, accepted, combined, ModuleContributionCatalog.Resolve(combined, reportRejection ?? (_ => { })));
    }

    private static void Append<T>(IEnumerable<T> items, ICollection<T> target)
    {
        foreach (var item in items) target.Add(item);
    }
}
