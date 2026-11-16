using System.Reflection;
using TypeWhisper.ModuleKit;

[assembly: AssemblyMetadata("TypeWhisperModules", "stub")]

namespace TypeWhisper.Modules;

/// <summary>The entry point the host calls at startup. This build includes no modules.</summary>
public static class ModuleEntry
{
    /// <summary>The interface version the modules were written against. The host loads none it does not support.</summary>
    public static ModuleKitVersion RequiredModuleKitVersion => ModuleKitVersion.Current;

    /// <summary>Returns the modules of this build, which are none.</summary>
    public static IReadOnlyList<IHostModule> Create() => [];
}
