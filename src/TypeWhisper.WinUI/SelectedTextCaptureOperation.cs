namespace TypeWhisper.WinUI;

internal interface ISelectedTextCaptureLease : IDisposable
{
    uint MarkerSequenceNumber { get; }
}

internal sealed record SelectedClipboardState(string? Text, uint SequenceNumber, bool SourceOwnerVerified);

// Implementations bind the target to the original HWND/PID. Clipboard ownership checks
// must be repeated atomically when accepting a copied sequence and restoring the lease.
internal interface ISelectedTextCapturePlatform
{
    bool TargetStillCurrent { get; }
    Task<bool> WaitModifiersReleasedAsync(TimeSpan timeout, CancellationToken ct);
    Task<ISelectedTextCaptureLease> BeginTemporaryAsync(string marker, CancellationToken ct);
    bool OwnsMarker(ISelectedTextCaptureLease lease);
    uint SendCopy();
    Task<SelectedClipboardState> ReadClipboardAsync(CancellationToken ct);
    bool AcceptCopiedSequence(ISelectedTextCaptureLease lease, SelectedClipboardState state);
    // Never restore over a newer foreign clipboard sequence. This method must drain even after cancellation.
    Task RestoreAsync(ISelectedTextCaptureLease lease);
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

internal sealed record SelectedTextCaptureOptions
{
    internal TimeSpan ModifierTimeout { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan CopyTimeout { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(25);
}

internal static class SelectedTextCaptureOperation
{
    internal const int MaxInputCharacters = 128 * 1024;

    internal static async Task<string> RunAsync(ISelectedTextCapturePlatform platform,
        CancellationToken ct = default, SelectedTextCaptureOptions? options = null)
    {
        options ??= new();
        if (options.ModifierTimeout <= TimeSpan.Zero || options.ModifierTimeout > TimeSpan.FromSeconds(2) ||
            options.CopyTimeout <= TimeSpan.Zero || options.CopyTimeout > TimeSpan.FromSeconds(1) ||
            options.PollInterval <= TimeSpan.Zero || options.PollInterval > options.CopyTimeout)
            throw new ArgumentOutOfRangeException(nameof(options));
        ct.ThrowIfCancellationRequested();
        if (!await platform.WaitModifiersReleasedAsync(options.ModifierTimeout, ct))
            throw new InvalidOperationException("Release the shortcut keys and try again.");
        ct.ThrowIfCancellationRequested();
        if (!platform.TargetStillCurrent)
            throw new InvalidOperationException("The selected-text target changed. Select the text again and retry.");
        var marker = "__typewhisper-selection-" + Guid.NewGuid().ToString("N") + "__";
        var lease = await platform.BeginTemporaryAsync(marker, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!platform.TargetStillCurrent || !platform.OwnsMarker(lease))
                throw new InvalidOperationException("The target or clipboard changed before copying. No selection was sent.");
            if (platform.SendCopy() != 4)
                throw new InvalidOperationException("The copy command could not be sent. Select the text and try again.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.CopyTimeout);
            var attempts = (int)Math.Ceiling(options.CopyTimeout / options.PollInterval);
            try
            {
                for (var attempt = 0; attempt <= attempts; attempt++)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!platform.TargetStillCurrent)
                        throw new InvalidOperationException("The selected-text target changed while copying. No text was sent.");
                    var state = await platform.ReadClipboardAsync(deadline.Token);
                    if (!platform.TargetStillCurrent)
                        throw new InvalidOperationException("The selected-text target changed while copying. No text was sent.");
                    if (state.SequenceNumber != lease.MarkerSequenceNumber)
                    {
                        if (!state.SourceOwnerVerified || !platform.AcceptCopiedSequence(lease, state))
                            throw new InvalidOperationException("Another app changed the clipboard. Its contents were not used or replaced.");
                        // Adopt only the verified sequence for cleanup even if cancellation arrived
                        // during the read. Cancellation still prevents returning or processing text.
                        deadline.Token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(state.Text) || state.Text == marker)
                            throw new InvalidOperationException("No selected text was copied. Select text in the target app and try again.");
                        if (state.Text.Length > MaxInputCharacters)
                            throw new InvalidOperationException("The selection exceeds 128 Ki characters. Select less text; nothing was truncated or sent.");
                        ct.ThrowIfCancellationRequested();
                        return state.Text;
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    if (attempt < attempts) await platform.DelayAsync(options.PollInterval, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new InvalidOperationException("The target did not provide selected text in time. No clipboard fallback was used."); }
            throw new InvalidOperationException("The target did not provide selected text in time. No clipboard fallback was used.");
        }
        finally
        {
            try { await platform.RestoreAsync(lease); }
            finally { lease.Dispose(); }
        }
    }
}
