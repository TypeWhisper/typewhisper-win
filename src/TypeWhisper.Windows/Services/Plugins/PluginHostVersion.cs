using System.Reflection;

namespace TypeWhisper.Windows.Services.Plugins;

internal static class PluginHostVersion
{
    internal static Version Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();
            var informationalVersion = assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (informationalVersion?.Contains("-dev", StringComparison.OrdinalIgnoreCase) == true)
                return new Version(9999, 0);

            return assembly?.GetName().Version ?? new Version(1, 0);
        }
    }
}
