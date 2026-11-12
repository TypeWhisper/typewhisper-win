using System.Runtime.InteropServices;
using TypeWhisper.Plugin.CohereTranscribe;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class NativeLibraryProbeTests
{
    [WindowsFact]
    public void DriverProbeFindsSystemLibraries()
    {
        Assert.True(CohereTranscribePlugin.IsNativeLibraryAvailable("kernel32.dll"));
        Assert.False(CohereTranscribePlugin.IsNativeLibraryAvailable($"typewhisper-missing-{Guid.NewGuid():N}.dll"));
    }

    [WindowsFact]
    public void DriverProbeIgnoresLibrariesOutsideTheSystemDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), "typewhisper-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var name = $"typewhisper-probe-{Guid.NewGuid():N}.dll";
        File.Copy(Path.Join(Environment.SystemDirectory, "version.dll"), Path.Join(directory, name));
        var path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            // Control: the default search order would find the copy through PATH.
            Assert.True(NativeLibrary.TryLoad(name, out var handle));
            NativeLibrary.Free(handle);

            Assert.False(CohereTranscribePlugin.IsNativeLibraryAvailable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
