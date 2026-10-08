using System.Collections.Concurrent;
using NAudio.Wave;
using TypeWhisper.WinUI.Platform;
using Xunit;

public sealed class StopRecordingThreadTests
{
    [Fact]
    public async Task StopRecordingAsyncFinishesTheStopOffTheCallingThread()
    {
        var input = new ThreadRecordingInput();
        using var audio = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), input, Timeout.InfiniteTimeSpan) { NormalizationEnabled = false };
        using var ui = new UiThread();
        var postsBeforeStop = -1;
        var stop = (ThreadId: -1, OnThreadPool: false, Posts: -1);
        input.OnStop = () => stop = (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread, ui.Posts);

        var samples = await ui.RunAsync(async () =>
        {
            Assert.True(audio.WarmUp());
            audio.StartRecording(enableRecovery: false);
            input.Feed(Enumerable.Repeat((short)3000, 480).ToArray());
            postsBeforeStop = ui.Posts;
            return await audio.StopRecordingAsync();
        });

        Assert.Equal(480, samples?.Length);
        Assert.NotEqual(-1, stop.ThreadId);
        // The stop (buffer copy, capture stop, normalization) must not run on the thread that owns the
        // caller's synchronization context, and nothing may be posted to that context to get there.
        Assert.NotEqual(ui.ThreadId, stop.ThreadId);
        Assert.True(stop.OnThreadPool);
        Assert.Equal(postsBeforeStop, stop.Posts);
    }

    // A dedicated thread owning a synchronization context, standing in for the WinUI dispatcher thread:
    // awaits that capture the context resume on this thread, and every such Post is counted.
    private sealed class UiThread : IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private int _posts;
        internal int ThreadId => _thread.ManagedThreadId;
        internal int Posts => Volatile.Read(ref _posts);
        internal UiThread()
        {
            _thread = new Thread(Pump) { IsBackground = true, Name = "Test UI thread" };
            _thread.Start();
        }
        private void Pump()
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }
        internal Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((_ =>
            {
                try
                {
                    action().ContinueWith(finished =>
                    {
                        if (finished.IsFaulted) completion.TrySetException(finished.Exception!.InnerExceptions);
                        else if (finished.IsCanceled) completion.TrySetCanceled();
                        else completion.TrySetResult(finished.Result);
                    }, TaskScheduler.Default);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }, null));
            return completion.Task;
        }
        private void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            _queue.Add((callback, state));
        }
        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
        private sealed class Context(UiThread owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state) => owner.Post(callback, state);
        }
    }

    private sealed class ThreadRecordingInput : IAudioInputCaptureFactory, IAudioInputCapture
    {
        private bool _running;
        internal Action? OnStop;
        public bool CanRestartAfterStop => true;
        public WaveFormat WaveFormat => new(16000, 16, 1);
        public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
        public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds) => this;
        public void Prepare() { }
        public void StartRecording() => _running = true;
        public void StopRecording()
        {
            _running = false;
            OnStop?.Invoke();
            RecordingStopped?.Invoke(this, new());
        }
        public void Dispose() => _running = false;
        public void Feed(short[] samples)
        {
            if (!_running) return;
            var data = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, data, 0, data.Length);
            DataAvailable?.Invoke(this, new(data, data.Length));
        }
    }
}