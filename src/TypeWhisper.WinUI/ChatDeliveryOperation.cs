using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI;

internal interface IChatDeliveryPlatform
{
    /// <summary>True while the game window is in the foreground and no modifier key is held.</summary>
    bool TargetIsReady { get; }
    /// <summary>Presses the keys in order and releases them in reverse; false when the chord was not fully sent.</summary>
    bool Press(IReadOnlyList<ushort> chord);
    /// <summary>Pastes the text into the focused chat field.</summary>
    Task<bool> InsertAsync(string text);
    /// <summary>Gives the game time to read the previous input.</summary>
    Task SettleAsync();
    /// <summary>Waits between two messages.</summary>
    Task PauseAsync(int milliseconds);
}

internal enum ChatDeliveryStage { Open, Insert, Submit }

/// <summary><paramref name="Sent"/> messages were fully delivered; <paramref name="FailedAt"/> names the step that stopped the rest.</summary>
internal readonly record struct ChatDeliveryResult(int Sent, int Total, ChatDeliveryStage? FailedAt)
{
    public bool Completed => FailedAt is null;
}

internal static class ChatDeliveryOperation
{
    internal const ushort EnterKey = 0x0D;
    private static readonly ushort[] Enter = [EnterKey];

    /// <summary>
    /// For every message: open the chat, insert the text, then press Enter. Each key is only sent while the game
    /// is still the foreground window, so a focus change never sends text or Enter to another app.
    /// </summary>
    internal static async Task<ChatDeliveryResult> RunAsync(IChatDeliveryPlatform platform, string text, ChatDeliveryOptions options,
        CancellationToken ct = default)
    {
        // Without Enter every part would land in the same field, so only sent messages are split.
        var parts = ChatMessageSplitter.Split(text, options.Submit ? options.MaxMessageBytes : null);
        IReadOnlyList<ushort> open = options.OpenKey switch
        {
            ChatOpenKey.Enter => Enter,
            ChatOpenKey.AddOn => options.AddOnChord,
            _ => []
        };
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                ct.ThrowIfCancellationRequested();
                await platform.SettleAsync();
                if (options.MessageIntervalMilliseconds > 0) await platform.PauseAsync(options.MessageIntervalMilliseconds);
                ct.ThrowIfCancellationRequested();
            }
            if (open.Count > 0)
            {
                if (!platform.TargetIsReady || !platform.Press(open)) return new(i, parts.Count, ChatDeliveryStage.Open);
                await platform.SettleAsync();
            }
            if (!platform.TargetIsReady || !await platform.InsertAsync(parts[i])) return new(i, parts.Count, ChatDeliveryStage.Insert);
            if (!options.Submit) continue;
            await platform.SettleAsync();
            if (!platform.TargetIsReady || !platform.Press(Enter)) return new(i, parts.Count, ChatDeliveryStage.Submit);
        }
        return new(parts.Count, parts.Count, null);
    }
}
