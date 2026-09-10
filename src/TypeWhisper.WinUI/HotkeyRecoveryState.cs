namespace TypeWhisper.WinUI;

internal enum HotkeyRecoverySignal { None, Suspend, Resume }

internal sealed class HotkeyRecoveryState
{
    private int _revision;
    private bool _disposed;
    internal static HotkeyRecoverySignal Classify(uint message, long reason) => (message, reason) switch
    {
        (0x218, 4) or (0x2B1, 2 or 4 or 7) => HotkeyRecoverySignal.Suspend,
        (0x218, 7 or 18) or (0x2B1, 1 or 3 or 8 or 15) => HotkeyRecoverySignal.Resume,
        _ => HotkeyRecoverySignal.None
    };
    internal int Invalidate() => ++_revision;
    internal bool IsCurrent(int revision) => !_disposed && revision == _revision;
    internal void Dispose() { _disposed = true; _revision++; }
}
