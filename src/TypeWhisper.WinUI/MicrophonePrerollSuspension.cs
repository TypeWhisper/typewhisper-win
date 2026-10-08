namespace TypeWhisper.WinUI;

// Independent reasons matter: waking the PC does not unlock its screen.
internal sealed class MicrophonePrerollSuspension
{
    private bool _sleeping, _disconnected;
    internal bool Observe(uint message, long reason, bool interactiveDesktop)
    {
        if (message == 0x218)
        {
            if (reason == 4) _sleeping = true;
            if (reason is 7 or 18) _sleeping = false;
        }
        if (message == 0x2B1)
        {
            if (reason is 2 or 4 or 6) _disconnected = true;
            if (reason is 1 or 3 or 5) _disconnected = false;
        }
        return _sleeping || _disconnected || (message == 0x2B1 && reason == 7) || !interactiveDesktop;
    }
}
