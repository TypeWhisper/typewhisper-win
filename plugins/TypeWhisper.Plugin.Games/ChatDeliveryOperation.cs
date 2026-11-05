using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Games;

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

/// <summary>Delivers into the game through the input TypeWhisper provides for the dictation's window.</summary>
internal sealed class TargetInputPlatform(IDictationTargetInput input, int settleMilliseconds, CancellationToken ct) : IChatDeliveryPlatform
{
    private readonly int _settle = Math.Clamp(settleMilliseconds, 0, 1000);

    public bool TargetIsReady => input.IsReady;
    public bool Press(IReadOnlyList<ushort> chord) => input.PressKeys(chord);
    public Task<bool> InsertAsync(string text) => input.PasteAsync(text, ct);

    // A hung or loading game would drop the next key; wait until its window answers, then give it a few frames.
    public async Task SettleAsync()
    {
        await input.WaitUntilResponsiveAsync(ct);
        if (_settle > 0) await Task.Delay(_settle, ct);
    }

    public Task PauseAsync(int milliseconds) => Task.Delay(Math.Clamp(milliseconds, 0, 5000), ct);
}

// TooLong: without Enter the text needs more than the one message the chat field holds, so nothing was inserted.
internal enum ChatDeliveryStage { Open, Insert, Submit, TooLong }

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
        var parts = ChatMessageSplitter.Split(text, options.MaxMessageBytes);
        // Without Enter every part would land in the same field, which keeps only one message. Inserting a longer text
        // would cut it while reporting success, so it is not inserted; the caller keeps it on the clipboard and in review.
        if (!options.Submit && parts.Count > 1) return new(0, parts.Count, ChatDeliveryStage.TooLong);
        var open = options.OpenKeys;
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
