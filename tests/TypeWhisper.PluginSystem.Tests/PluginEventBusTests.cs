using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginEventBusTests
{
    private readonly PluginEventBus _bus = new();

    [Fact]
    public async Task Subscribe_ReceivesPublishedEvent()
    {
        var tcs = new TaskCompletionSource<RecordingStartedEvent>();

        _bus.Subscribe<RecordingStartedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        var published = new RecordingStartedEvent();
        _bus.Publish(published);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(published.Timestamp, received.Timestamp);
    }

    [Fact]
    public async Task Subscribe_ReceivesCorrectEventType()
    {
        var tcs = new TaskCompletionSource<TranscriptionCompletedEvent>();
        var wrongTypeCalled = false;

        _bus.Subscribe<RecordingStartedEvent>(_ =>
        {
            wrongTypeCalled = true;
            return Task.CompletedTask;
        });

        _bus.Subscribe<TranscriptionCompletedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        var published = new TranscriptionCompletedEvent { Text = "hello" };
        _bus.Publish(published);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("hello", received.Text);

        // Give a moment for the wrong handler to potentially fire
        await Task.Delay(100);
        Assert.False(wrongTypeCalled);
    }

    [Fact]
    public async Task MultipleSubscribers_AllReceiveEvent()
    {
        var tcs1 = new TaskCompletionSource<RecordingStartedEvent>();
        var tcs2 = new TaskCompletionSource<RecordingStartedEvent>();

        _bus.Subscribe<RecordingStartedEvent>(e =>
        {
            tcs1.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Subscribe<RecordingStartedEvent>(e =>
        {
            tcs2.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStartedEvent());

        await Task.WhenAll(
            tcs1.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            tcs2.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(tcs1.Task.IsCompletedSuccessfully);
        Assert.True(tcs2.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Publish_WithNoSubscribers_DoesNotThrow()
    {
        var ex = Record.Exception(() => _bus.Publish(new RecordingStartedEvent()));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_Subscription_StopsReceivingEvents()
    {
        var callCount = 0;

        var subscription = _bus.Subscribe<RecordingStartedEvent>(_ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStartedEvent());
        await Task.Delay(200);
        Assert.Equal(1, callCount);

        subscription.Dispose();

        _bus.Publish(new RecordingStartedEvent());
        await Task.Delay(200);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Dispose_Subscription_TwiceDoesNotThrow()
    {
        var subscription = _bus.Subscribe<RecordingStartedEvent>(_ => Task.CompletedTask);

        var ex = Record.Exception(() =>
        {
            subscription.Dispose();
            subscription.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task ExceptionInHandler_DoesNotAffectOtherHandlers()
    {
        var tcs = new TaskCompletionSource<bool>();

        _bus.Subscribe<RecordingStartedEvent>(_ =>
        {
            throw new InvalidOperationException("Boom!");
        });

        _bus.Subscribe<RecordingStartedEvent>(_ =>
        {
            tcs.SetResult(true);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStartedEvent());

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result);
    }

    [Fact]
    public async Task Publish_EventDataIsPreserved()
    {
        var tcs = new TaskCompletionSource<RecordingStoppedEvent>();

        _bus.Subscribe<RecordingStoppedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStoppedEvent { DurationSeconds = 42.5 });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(42.5, received.DurationSeconds);
    }

    [Fact]
    public async Task ConcurrentPublishAndSubscribe_DoesNotThrow()
    {
        var received = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start multiple subscribers concurrently
        var subscriptions = new List<IDisposable>();
        var subscribeTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            var sub = _bus.Subscribe<RecordingStartedEvent>(_ =>
            {
                Interlocked.Increment(ref received);
                return Task.CompletedTask;
            });
            lock (subscriptions)
            {
                subscriptions.Add(sub);
            }
        }));

        // Start multiple publishes concurrently
        var publishTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            _bus.Publish(new RecordingStartedEvent());
        }));

        var ex = await Record.ExceptionAsync(async () =>
        {
            await Task.WhenAll(subscribeTasks.Concat(publishTasks));
        });

        Assert.Null(ex);

        // Cleanup
        foreach (var sub in subscriptions)
        {
            sub.Dispose();
        }
    }

    [Fact]
    public async Task TranscriptionCompletedEvent_FullPayload()
    {
        var tcs = new TaskCompletionSource<TranscriptionCompletedEvent>();

        _bus.Subscribe<TranscriptionCompletedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new TranscriptionCompletedEvent
        {
            Text = "Hello world",
            DetectedLanguage = "en",
            DurationSeconds = 3.5,
            ModelId = "whisper-large-v3"
        });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hello world", received.Text);
        Assert.Equal("en", received.DetectedLanguage);
        Assert.Equal(3.5, received.DurationSeconds);
        Assert.Equal("whisper-large-v3", received.ModelId);
    }

    [Fact]
    public async Task TextInsertedEvent_IsDelivered()
    {
        var tcs = new TaskCompletionSource<TextInsertedEvent>();

        _bus.Subscribe<TextInsertedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new TextInsertedEvent { Text = "inserted", TargetApp = "notepad" });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("inserted", received.Text);
        Assert.Equal("notepad", received.TargetApp);
    }

    [Fact]
    public async Task TranscriptionFailedEvent_IsDelivered()
    {
        var tcs = new TaskCompletionSource<TranscriptionFailedEvent>();

        _bus.Subscribe<TranscriptionFailedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new TranscriptionFailedEvent { ErrorMessage = "timeout", ModelId = "m1" });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("timeout", received.ErrorMessage);
        Assert.Equal("m1", received.ModelId);
    }
}
