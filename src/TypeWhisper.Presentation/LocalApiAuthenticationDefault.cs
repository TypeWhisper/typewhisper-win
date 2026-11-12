namespace TypeWhisper.Presentation;

/// <summary>The local API requires its token by default. Settings saved before that default keep running
/// without a token only while the API was already on, so existing scripts and integrations keep working; a server
/// that was off asks for the token once it is turned on.</summary>
public static class LocalApiAuthenticationDefault
{
    /// <summary>The settings version that introduced the token requirement by default.</summary>
    public const int Version = 2;

    /// <summary>Returns whether the token is required for settings saved with <paramref name="savedVersion"/>.</summary>
    public static bool RequiresToken(bool enabled, bool savedRequirement, int savedVersion) =>
        savedVersion >= Version ? savedRequirement : savedRequirement || !enabled;
}
