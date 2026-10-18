namespace TypeWhisper.Presentation;

/// <summary>A single startup opportunity. User activity permanently cancels it for this process.</summary>
public sealed class WhatsNewSession
{
    private readonly WhatsNewPreferencesStore _store;
    private readonly int _contentVersion;
    private int _available;

    /// <summary>Whether preparing a window is useful; TryShow rechecks after preparation.</summary>
    public bool Pending => Volatile.Read(ref _available) != 0;

    /// <summary>A persistence failure, without a modal error or a retry during this launch.</summary>
    public Exception? Error => _store.Error;

    /// <summary>Allows only ordinary foreground launches by users who already completed setup.</summary>
    public WhatsNewSession(WhatsNewPreferencesStore store, ApplicationActivationRequest request,
        bool setupCompleted, int contentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentVersion);
        _store = store;
        _contentVersion = contentVersion;
        // Fresh/incomplete setup already introduces this content. Never append an update tour to it.
        if (!setupCompleted) store.RecordSeen(contentVersion);
        _available = setupCompleted && store.Error is null && store.LastSeenContentVersion < contentVersion
            && request.ShowWindow && request.Route is null && request.Files.Count == 0
            && request.AccountCallback is null && request.SettingsCategory is null && request.Error is null ? 1 : 0;
    }

    /// <summary>May be called directly from an activation or keyboard callback without doing I/O or UI work.</summary>
    public void Suppress() => Interlocked.Exchange(ref _available, 0);

    /// <summary>Consumes the sole opportunity, requiring durable progress before automatic presentation.</summary>
    public bool TryShow(bool busy, Action show)
    {
        if (busy) Suppress();
        if (Interlocked.Exchange(ref _available, 0) == 0) return false;
        // Persist first: a readable but unwritable file must not show the same news on every launch.
        // A failed presentation stays consumed; optional news remains available through the About page.
        _store.RecordSeen(_contentVersion);
        if (_store.Error is not null) return false;
        show();
        return true;
    }

    /// <summary>Explicit viewing ignores the startup opportunity and seen version, but still yields to active work.</summary>
    public bool TryShowManually(bool busy, Action show)
    {
        Suppress();
        if (busy) return false;
        show();
        _store.RecordSeen(_contentVersion);
        return true;
    }
}
