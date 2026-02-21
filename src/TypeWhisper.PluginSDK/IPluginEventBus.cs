using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Publish/subscribe event bus for plugin communication.
/// </summary>
public interface IPluginEventBus
{
    /// <summary>Publishes an event to all subscribers of type <typeparamref name="T"/>.</summary>
    void Publish<T>(T pluginEvent) where T : PluginEvent;

    /// <summary>
    /// Subscribes to events of type <typeparamref name="T"/>.
    /// Dispose the returned handle to unsubscribe.
    /// </summary>
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent;
}
