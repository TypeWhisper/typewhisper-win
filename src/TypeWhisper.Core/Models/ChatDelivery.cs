using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>
/// Lists the ways TypeWhisper can open a game's text chat before it inserts dictated text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatOpenKey>))]
public enum ChatOpenKey
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
public sealed record ChatDeliveryOptions
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
