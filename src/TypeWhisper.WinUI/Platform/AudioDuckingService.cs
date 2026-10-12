using NAudio.CoreAudioApi;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Lowers other apps on the output while recording; TypeWhisper's own audio keeps its volume.
/// </summary>
public sealed class AudioDuckingService : IAudioDuckingService
{
    private const int ElementNotFound = unchecked((int)0x80070490);
    private readonly object _gate = new();
    private readonly object _queueGate = new();
    private readonly AudioSessionDucking _sessions;
    private readonly string _journalPath;
    // Used only on the work queue.
    private bool _journalLoaded;
    private Task _work = Task.CompletedTask;
    private bool _closed;
    // Set under the gate once shutdown began; notifications that arrive later change nothing.
    private bool _stopped;
    // Held while ducked so sessions that start during the recording are reported.
    private MMDevice? _device;
    private AudioSessionManager? _manager;
    private string? _duckedDeviceId;
    // Held while an interrupted run's entries wait for their app to play again on that output.
    private readonly Dictionary<string, (MMDevice Device, AudioSessionManager Manager)> _recovery = new(StringComparer.Ordinal);
    // Registered while ducked or while such entries are pending; used only on the work queue.
    private (MMDeviceEnumerator Enumerator, MMDeviceNotificationClient Client)? _outputWatch;

    /// <summary>Gets or sets the output endpoint to attenuate; null or empty selects the default multimedia endpoint.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// Creates the service and restores apps that an interrupted earlier run left lowered.
    /// </summary>
    public AudioDuckingService(string journalPath)
    {
        _journalPath = journalPath;
        _sessions = new((uint)Environment.ProcessId, entries => SaveJournal(journalPath, entries), () => DateTimeOffset.UtcNow, AppDiagnostics.Write);
        Enqueue(() => { if (LoadJournal()) RecoverPending(); });
    }

    // Saving would replace a journal that could not be read, so nothing is lowered until it has been read.
    private bool LoadJournal()
    {
        if (_journalLoaded) return true;
        IReadOnlyList<AudioDuckingJournalEntry> previous;
        try { previous = AudioDuckingJournal.Load(_journalPath); }
        // An unreadable journal was moved aside, so a new one can be started.
        catch (System.Text.Json.JsonException ex) { AppDiagnostics.Write("audio.ducking.journal-unreadable", ex); previous = []; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.journal-failed", ex); return false; }
        lock (_gate) _sessions.Resume(previous);
        return _journalLoaded = true;
    }

    /// <summary>
    /// Ducks audio.
    /// </summary>
    public void DuckAudio(float factor)
    {
        var deviceId = OutputDeviceId;
        Enqueue(() => Duck(deviceId, factor));
    }

    /// <summary>
    /// Restores audio.
    /// </summary>
    public void RestoreAudio() => Enqueue(Restore);

    /// <summary>
    /// Restores lowered apps, stops listening for sessions and outputs, and completes once nothing more is written.
    /// Later requests are ignored.
    /// </summary>
    public Task ShutdownAsync()
    {
        lock (_queueGate)
        {
            if (!_closed)
            {
                Enqueue(Close);
                _closed = true;
            }
            return _work;
        }
    }

    // Session notifications are only delivered to registrations made on an MTA thread, and the work stays
    // off the dictation path and the Core Audio notification threads. Requests run in order, so a quick stop
    // never restores before its duck. Returns false once shut down.
    private bool Enqueue(Action action)
    {
        lock (_queueGate)
        {
            if (_closed) return false;
            _work = _work.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.failed", ex); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return true;
        }
    }

    private void Duck(string? deviceId, float factor)
    {
        lock (_gate) if (_device is not null) return;
        if (!LoadJournal()) return;
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
        AudioSessionManager manager;
        try { manager = device.AudioSessionManager; }
        catch { device.Dispose(); throw; }
        // Sessions reported meanwhile are queued behind this duck and added after it.
        lock (_gate) (_device, _manager, _duckedDeviceId) = (device, manager, device.ID);
        List<IDuckableAudioSession> sessions;
        try
        {
            manager.OnSessionCreated += OnSessionCreated;
            sessions = SessionsAfterSubscribing(enumerator, manager, device.ID);
        }
        catch
        {
            // Nothing was lowered yet, so the listener is taken back and the next recording tries again.
            lock (_gate) (_device, _manager, _duckedDeviceId) = (null, null, null);
            Release(device, manager, OnSessionCreated);
            throw;
        }
        lock (_gate)
        {
            _sessions.Duck(device.ID, factor, sessions);
            ReleaseUnheld(sessions);
        }
        RecoverPending();
    }

    private void OnSessionCreated(object sender, AudioSessionControl created)
    {
        try
        {
            // Lowering writes the journal, which is kept off the notification thread.
            if (Session.TryCreate(created) is { } session && !Enqueue(() => AddCreated(sender, session))) session.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.session-failed", ex); }
    }

    private void AddCreated(object sender, IDuckableAudioSession session)
    {
        lock (_gate)
        {
            if (!_stopped && sender == _manager) _sessions.Add(session);
            ReleaseUnheld([session]);
        }
    }

    // An output that was disconnected during the recording comes back with a new session manager; the old one
    // reports nothing, so apps that start there are listened for again.
    private void ListenAgainWhileDucked(string deviceId)
    {
        lock (_gate) if (_device is null || _duckedDeviceId != deviceId) return;
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        if (device.State != DeviceState.Active) { device.Dispose(); return; }
        AudioSessionManager manager;
        try { manager = device.AudioSessionManager; }
        catch { device.Dispose(); throw; }
        // Set up completely before it replaces the previous listener, so a failure keeps that one. Sessions it
        // reports meanwhile are queued behind this and find it in place.
        List<IDuckableAudioSession> sessions;
        try
        {
            manager.OnSessionCreated += OnSessionCreated;
            sessions = SessionsAfterSubscribing(enumerator, manager, deviceId);
        }
        catch { Release(device, manager, OnSessionCreated); throw; }
        (MMDevice Device, AudioSessionManager? Manager) previous;
        lock (_gate)
        {
            if (_device is null || _duckedDeviceId != deviceId) previous = (device, manager);
            else
            {
                previous = (_device, _manager);
                (_device, _manager) = (device, manager);
            }
            if (!_stopped && manager == _manager)
                foreach (var session in sessions) _sessions.Add(session);
            ReleaseUnheld(sessions);
        }
        Release(previous.Device, previous.Manager, OnSessionCreated);
    }

    private void Restore()
    {
        MMDevice? device;
        AudioSessionManager? manager;
        lock (_gate)
        {
            ReleaseUnheld(_sessions.Restore());
            (device, manager) = (_device, _manager);
            (_device, _manager, _duckedDeviceId) = (null, null, null);
        }
        if (device is not null) Release(device, manager, OnSessionCreated);
        RecoverPending();
    }

    // Restores what an interrupted run left lowered and keeps listening on each output until every such app
    // has played again. Output changes are watched meanwhile, and while ducked, so an output that is connected
    // later, or disconnected and connected again, gets a new listener.
    private void RecoverPending()
    {
        List<string> devices;
        bool watch;
        var idle = new List<(MMDevice Device, AudioSessionManager Manager)>();
        lock (_gate)
        {
            if (_stopped) return;
            var pendingDevices = _sessions.PendingDevices;
            foreach (var deviceId in _recovery.Keys.Except(pendingDevices).ToList())
            {
                idle.Add(_recovery[deviceId]);
                _recovery.Remove(deviceId);
            }
            devices = [.. pendingDevices.Except(_recovery.Keys)];
            watch = pendingDevices.Count > 0 || _device is not null;
        }
        foreach (var (device, manager) in idle) Release(device, manager, OnRecoverySessionCreated);
        // Watched before the outputs are checked, so one that becomes active meanwhile is reported.
        WatchOutputs(watch);
        if (devices.Count > 0)
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var deviceId in devices)
            {
                try { Listen(enumerator, deviceId); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A removed output is not an error; the watch reports it once it is added again.
                    if (ex.HResult != ElementNotFound) AppDiagnostics.Write("audio.ducking.recover-failed", ex);
                }
            }
        }
    }

    // A listener on an output that changed state may no longer receive sessions, so it is replaced.
    private void OutputChanged(string deviceId)
    {
        (MMDevice Device, AudioSessionManager Manager) listener;
        lock (_gate) if (!_recovery.Remove(deviceId, out listener)) listener = default;
        if (listener.Device is not null) Release(listener.Device, listener.Manager, OnRecoverySessionCreated);
        try { ListenAgainWhileDucked(deviceId); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex.HResult != ElementNotFound) AppDiagnostics.Write("audio.ducking.session-failed", ex);
        }
        RecoverPending();
    }

    private void Listen(MMDeviceEnumerator enumerator, string deviceId)
    {
        var device = enumerator.GetDevice(deviceId);
        if (device.State != DeviceState.Active) { device.Dispose(); return; }
        AudioSessionManager manager;
        try { manager = device.AudioSessionManager; }
        catch { device.Dispose(); throw; }
        // Registered before subscribing, so a session reported while the existing ones are listed is not dropped.
        // From here on RecoverPending releases it once nothing is pending for this output.
        lock (_gate) _recovery[deviceId] = (device, manager);
        List<IDuckableAudioSession> sessions;
        try
        {
            manager.OnSessionCreated += OnRecoverySessionCreated;
            sessions = SessionsAfterSubscribing(enumerator, manager, deviceId);
        }
        catch
        {
            // Without a working listener the next RecoverPending tries again.
            lock (_gate) if (_recovery.TryGetValue(deviceId, out var current) && current.Manager == manager) _recovery.Remove(deviceId);
            Release(device, manager, OnRecoverySessionCreated);
            throw;
        }
        bool settled;
        lock (_gate)
        {
            _sessions.Recover(deviceId, sessions);
            ReleaseUnheld(sessions);
            settled = !_sessions.PendingDevices.Contains(deviceId) && _recovery.Remove(deviceId);
        }
        if (settled) Release(device, manager, OnRecoverySessionCreated);
    }

    // Notifications that arrive while this runs see the stop flag, so no callback writes the journal afterwards,
    // even when unregistering fails.
    private void Close()
    {
        lock (_gate) _stopped = true;
        Restore();
        List<(MMDevice Device, AudioSessionManager Manager)> listeners;
        lock (_gate)
        {
            listeners = [.. _recovery.Values];
            _recovery.Clear();
        }
        foreach (var (device, manager) in listeners) Release(device, manager, OnRecoverySessionCreated);
        WatchOutputs(false);
    }

    private void WatchOutputs(bool watch)
    {
        if (watch == _outputWatch is not null) return;
        if (watch)
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                // Events arrive on the Core Audio notification thread and only queue work.
                var client = enumerator.CreateNotificationClient(useSynchronizationContext: false);
                client.DeviceStateChanged += (_, e) => Enqueue(() => OutputChanged(e.DeviceId));
                client.DeviceAdded += (_, e) => Enqueue(() => OutputChanged(e.DeviceId));
                client.DeviceRemoved += (_, e) => Enqueue(() => OutputChanged(e.DeviceId));
                _outputWatch = (enumerator, client);
            }
            catch { enumerator.Dispose(); throw; }
        }
        else
        {
            var (enumerator, client) = _outputWatch!.Value;
            _outputWatch = null;
            using (enumerator)
            {
                try { client.Dispose(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.release-failed", ex); }
            }
        }
    }

    private void OnRecoverySessionCreated(object sender, AudioSessionControl created)
    {
        try
        {
            if (Session.TryCreate(created) is { } session && !Enqueue(() => RecoverCreated(sender, session))) session.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.recover-failed", ex); }
    }

    private void RecoverCreated(object sender, Session session)
    {
        bool settled;
        using (session)
        lock (_gate)
        {
            if (_stopped || _recovery.FirstOrDefault(listener => listener.Value.Manager == sender).Key is not { } deviceId) return;
            _sessions.Recover(deviceId, [session]);
            settled = !_sessions.PendingDevices.Contains(deviceId);
        }
        if (settled) RecoverPending();
    }

    // Called under the gate. Sessions wrap native controls that are released here rather than by the collector.
    private void ReleaseUnheld(IEnumerable<IDuckableAudioSession> sessions)
    {
        foreach (var session in sessions.Where(session => !_sessions.Holds(session)))
            ((Session)session).Dispose();
    }

    // Windows reports sessions only once the subscribed manager's list was first counted. Listing through a
    // fresh manager after that leaves no session between the list and the notifications.
    private static List<IDuckableAudioSession> SessionsAfterSubscribing(MMDeviceEnumerator enumerator, AudioSessionManager subscribed, string deviceId)
    {
        _ = subscribed.Sessions.Count;
        using var listing = enumerator.GetDevice(deviceId);
        using var manager = listing.AudioSessionManager;
        return Sessions(manager);
    }

    // A failure here is logged, so the remaining listeners are still released.
    private static void Release(MMDevice device, AudioSessionManager? manager, AudioSessionManager.SessionCreatedDelegate handler)
    {
        using (device)
        {
            try
            {
                if (manager is not null) manager.OnSessionCreated -= handler;
                manager?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.release-failed", ex); }
        }
    }

    // System sounds are lowered as well, as they are with the system volume on the Mac.
    private static List<IDuckableAudioSession> Sessions(AudioSessionManager manager)
    {
        var sessions = manager.Sessions;
        var result = new List<IDuckableAudioSession>(sessions.Count);
        for (var i = 0; i < sessions.Count; i++)
        {
            try { if (Session.TryCreate(sessions[i]) is { } session) result.Add(session); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.session-failed", ex); }
        }
        return result;
    }

    private static bool SaveJournal(string path, IReadOnlyList<AudioDuckingJournalEntry> entries)
    {
        try { AudioDuckingJournal.Save(path, entries); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.journal-failed", ex); return false; }
    }

    private sealed class Session : IDuckableAudioSession, IDisposable
    {
        private readonly AudioSessionControl _control;

        private Session(AudioSessionControl control)
        {
            _control = control;
            SessionId = control.GetSessionIdentifier;
            InstanceId = control.GetSessionInstanceIdentifier;
            ProcessId = control.GetProcessID;
        }

        public string SessionId { get; }
        public string InstanceId { get; }
        public uint ProcessId { get; }
        public float Volume
        {
            get => _control.SimpleAudioVolume.Volume;
            set => _control.SimpleAudioVolume.Volume = value;
        }

        public void Dispose()
        {
            try { _control.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.release-failed", ex); }
        }

        // An expired session belongs to an app that stopped playing; it is no longer in the volume mixer.
        internal static Session? TryCreate(AudioSessionControl control)
        {
            try
            {
                if (control.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) return new(control);
            }
            catch { control.Dispose(); throw; }
            control.Dispose();
            return null;
        }
    }
}
