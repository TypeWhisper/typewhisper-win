using System.Runtime.Loader;
using System.Text.Json;
using Moq;
using NAudio.CoreAudioApi;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed class PortablePluginAudioInteropTests
{
    [Fact]
    public async Task PackageUsesHostAudioTypesAfterHostComActivation()
    {
        var root = Path.Combine(Path.GetTempPath(), "typewhisper-audio-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var fixture = typeof(TtsProbePlugin).Assembly;
            File.Copy(fixture.Location, Path.Combine(root, "fixture.dll"));
            var audio = typeof(MMDeviceEnumerator).Assembly;
            File.Copy(audio.Location, Path.Combine(root, Path.GetFileName(audio.Location)));
            File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
            {
                Id = "test.typewhisper.runtime", Name = "Speech fixture", Version = "1.0.0",
                AssemblyName = "fixture.dll", PluginClass = typeof(TtsProbePlugin).FullName!
            }));
            // Ensure the package can resolve its private DLL, so fallback to the default
            // context cannot make the test pass without the explicit sharing rule.
            Assert.NotNull(new AssemblyDependencyResolver(Path.Combine(root, "fixture.dll")).ResolveAssemblyToPath(audio.GetName()));
            using var hostEnumerator = new MMDeviceEnumerator();
            await using var package = await PortablePluginPackage.LoadAsync(root, Mock.Of<IPluginHostServices>(), new(1, 1, 4));
            var context = AssemblyLoadContext.GetLoadContext(package.Plugin.GetType().Assembly)!;
            var resolved = context.LoadFromAssemblyName(audio.GetName());
            Assert.Same(audio, resolved);
            using var pluginEnumerator = (IDisposable)Activator.CreateInstance(resolved.GetType(typeof(MMDeviceEnumerator).FullName!)!)!;
        }
        finally
        {
            // Collectible assemblies can remain mapped until a later GC.
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
