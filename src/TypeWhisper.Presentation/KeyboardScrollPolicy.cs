namespace TypeWhisper.Presentation;

/// <summary>Calculates bounded offsets for vertical keyboard page navigation.</summary>
public static class KeyboardScrollPolicy
{
    /// <summary>Returns the target offset for a Windows navigation key, or null for other keys.</summary>
    public static double? Target(int key, double offset, double viewport, double maximum)
    {
        var page = Math.Max(48, viewport * .85);
        double? next = key switch
        {
            0x26 => offset - 64,
            0x28 => offset + 64,
            0x21 => offset - page,
            0x22 => offset + page,
            0x24 => 0,
            0x23 => maximum,
            _ => null
        };
        return next is { } target ? Math.Clamp(target, 0, Math.Max(0, maximum)) : null;
    }
}
