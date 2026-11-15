namespace TypeWhisper.ModuleKit;

/// <summary>The host capabilities a module can use. More capabilities are added as the host offers them.</summary>
public interface IModuleHost
{
    /// <summary>The host's informational version, such as <c>"1.3.0"</c>.</summary>
    string HostVersion { get; }
}
