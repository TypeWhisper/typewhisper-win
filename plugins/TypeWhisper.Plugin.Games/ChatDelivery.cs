namespace TypeWhisper.Plugin.Games;

/// <summary>
/// Lists the ways the plugin can open a game's text chat before it inserts dictated text.
/// </summary>
internal enum ChatOpenKey
{
    /// <summary>
    /// Presses nothing; the player opens the chat before dictating.
    /// </summary>
    None,
    /// <summary>
    /// Presses Enter. An open chat closes again, so dictation must start with the chat closed.
    /// </summary>
    Enter,
    /// <summary>
    /// Presses the key of the TypeWhisper game add-on, which opens a closed chat and leaves an open one as it is.
    /// </summary>
    AddOn
}

/// <summary>
/// Describes how dictated text is delivered into a game chat.
/// </summary>
internal sealed record ChatDeliveryOptions
{
    /// <summary>
    /// Gets how the chat is opened.
    /// </summary>
    public ChatOpenKey OpenKey { get; init; } = ChatOpenKey.Enter;
    /// <summary>
    /// Gets the virtual keys of the chord the game add-on listens for, modifiers first; used when <see cref="OpenKey"/>
    /// is <see cref="ChatOpenKey.AddOn"/>.
    /// </summary>
    public IReadOnlyList<ushort> AddOnChord { get; init; } = [];
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

/// <summary>How a dictation into a game's chat runs, from the game and its add-on's settings.</summary>
internal static class GameChat
{
    /// <summary>Returns the chat delivery for the game. Only the add-on opens a chat that is already open without
    /// closing it; without it, Enter opens the chat.</summary>
    public static ChatDeliveryOptions ChatFor(GameDefinition game, WowAddOnSettings addOn, bool addOnInstalled) => new()
    {
        OpenKey = addOnInstalled ? ChatOpenKey.AddOn : ChatOpenKey.Enter, AddOnChord = game.AddOnChord, Submit = addOn.Send,
        MaxMessageBytes = game.MaxMessageBytes, MessageIntervalMilliseconds = game.MessageIntervalMilliseconds
    };

    /// <summary>Returns the chords for the add-on's cast bar, or null when TypeWhisper's own indicator shows the
    /// dictation. Only an installed add-on with its bar turned on draws one.</summary>
    public static GameIndicatorChords? IndicatorFor(GameDefinition game, WowAddOnSettings addOn, bool addOnInstalled) =>
        addOnInstalled && addOn.ShowBar ? game.Indicator : null;
}
