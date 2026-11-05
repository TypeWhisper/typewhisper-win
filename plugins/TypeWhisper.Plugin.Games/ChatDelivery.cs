namespace TypeWhisper.Plugin.Games;

/// <summary>
/// Describes how dictated text is delivered into a game chat.
/// </summary>
internal sealed record ChatDeliveryOptions
{
    /// <summary>
    /// Gets the virtual keys that open the chat, modifiers first: Enter, a game's chat key, or the chord of the
    /// TypeWhisper add-on, which opens a closed chat and leaves an open one as it is. Empty when the player opens the
    /// chat before dictating. Enter or a chat key closes a chat that is already open, so dictation must start with
    /// the chat closed.
    /// </summary>
    public IReadOnlyList<ushort> OpenKeys { get; init; } = [ChatDeliveryOperation.EnterKey];
    /// <summary>
    /// Gets whether Enter sends each message.
    /// </summary>
    public bool Submit { get; init; } = true;
    /// <summary>
    /// Gets the longest message the game accepts in UTF-8 bytes, or null when it has no limit.
    /// </summary>
    public int? MaxMessageBytes { get; init; }
    /// <summary>
    /// Gets how long the game gets to read each key, in milliseconds.
    /// Games expose no chat state, so this covers the frames they need after the window is responsive again.
    /// </summary>
    public int SettleMilliseconds { get; init; } = 100;
    /// <summary>
    /// Gets the pause between two messages of one dictation, in milliseconds, so the game's chat throttle keeps them.
    /// </summary>
    public int MessageIntervalMilliseconds { get; init; }
}

/// <summary>How a dictation into the World of Warcraft chat runs, from its add-on's settings.</summary>
internal static class GameChat
{
    /// <summary>Returns the chat delivery for the game. Only the add-on opens a chat that is already open without
    /// closing it; without it, Enter opens the chat.</summary>
    public static ChatDeliveryOptions ChatFor(GameDefinition game, WowAddOnSettings addOn, bool addOnInstalled) => new()
    {
        OpenKeys = addOnInstalled ? game.AddOnChord : [ChatDeliveryOperation.EnterKey], Submit = addOn.Send,
        MaxMessageBytes = game.MaxMessageBytes, MessageIntervalMilliseconds = game.MessageIntervalMilliseconds
    };

    /// <summary>Returns the chords for the add-on's cast bar, or null when TypeWhisper's own indicator shows the
    /// dictation. Only an installed add-on with its bar turned on draws one.</summary>
    public static GameIndicatorChords? IndicatorFor(GameDefinition game, WowAddOnSettings addOn, bool addOnInstalled) =>
        addOnInstalled && addOn.ShowBar ? game.Indicator : null;
}
