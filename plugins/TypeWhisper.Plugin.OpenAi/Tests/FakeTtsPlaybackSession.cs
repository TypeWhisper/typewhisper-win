using TypeWhisper.PluginSDK;
namespace TypeWhisper.PluginSystem.Tests;
internal sealed class FakeTtsPlaybackSession : ITtsPlaybackSession
{
    private int _stopped;

    public bool IsActive => Volatile.Read(ref _stopped) == 0;
    public int StopCount { get; private set; }
    public event EventHandler? Completed;

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        StopCount++;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
