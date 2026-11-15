using TypeWhisper.ModuleKit;
using TypeWhisper.Modules;

namespace TypeWhisper.WinUI;

// The modules assembly this app was built with: the optional modules or the stub (see TypeWhisper.WinUI.csproj).
internal sealed partial class HostModules
{
    // Known without loading the modules, so views can read it at any time.
    internal static bool StubLinked { get; } = BuildKindOf(typeof(ModuleEntry).Assembly) == StubBuildKind;

    internal static HostModules Load(IModuleHost host) =>
        Load(host, typeof(ModuleEntry).Assembly, ModuleEntry.Create, AppDiagnostics.WriteFailure);
}

internal sealed class WinUIModuleHost : IModuleHost
{
    public string HostVersion => WindowsApplicationUpdates.CurrentVersion;
}
