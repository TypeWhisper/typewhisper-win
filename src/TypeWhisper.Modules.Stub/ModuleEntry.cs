using System.Reflection;
using TypeWhisper.ModuleKit;

[assembly: AssemblyMetadata("TypeWhisperModules", "stub")]

namespace TypeWhisper.Modules;

/// <summary>The entry point the host calls at startup. This build includes no modules.</summary>
public static class ModuleEntry
{
    /// <summary>Returns the modules of this build, which are none.</summary>
    public static IReadOnlyList<IHostModule> Create() => [];
}
