using System.Collections.Concurrent;
using System.Diagnostics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Thread-safe publish/subscribe event bus for plugin communication.
/// Handlers are invoked asynchronously (fire-and-forget) so publishers are not blocked.
/// </summary>
public sealed class PluginEventBus : IPluginEventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<object, Task>>> _handlers = new();
    private readonly object _lock = new();

    public void Publish<T>(T pluginEvent) where T : PluginEvent
    {
        var eventType = typeof(T);
        if (!_handlers.TryGetValue(eventType, out var handlers))
            return;

        List<Func<object, Task>> snapshot;
        lock (_lock)
        {
            snapshot = [.. handlers];
        }

        foreach (var handler in snapshot)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(pluginEvent);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginEventBus] Handler for {eventType.Name} threw: {ex.Message}");
                }
            });
        }
    }

    public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent
    {
        var eventType = typeof(T);
        Func<object, Task> wrappedHandler = obj => handler((T)obj);

        lock (_lock)
        {
            var handlers = _handlers.GetOrAdd(eventType, _ => []);
            handlers.Add(wrappedHandler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(eventType, out var handlers))
                {
                    handlers.Remove(wrappedHandler);
                }
            }
        });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
