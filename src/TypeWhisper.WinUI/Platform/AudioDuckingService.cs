using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
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
    private Task _work = Task.CompletedTask;
    // Held while ducked so sessions that start during the recording are reported.
    private MMDevice? _device;
    private AudioSessionManager? _manager;
    // Sessions that started while the existing ones were being listed.
    private List<IDuckableAudioSession>? _starting;
    // Held while an interrupted run's entries wait for their app to play again on that output.
    private readonly Dictionary<string, (MMDevice Device, AudioSessionManager Manager)> _recovery = new(StringComparer.Ordinal);
    // Registered while such an output is not connected; used only on the work queue.
    private (MMDeviceEnumerator Enumerator, OutputWatch Watcher)? _outputWatch;

    /// <summary>Gets or sets the output endpoint to attenuate; null or empty selects the default multimedia endpoint.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// Creates the service and restores apps that an interrupted earlier run left lowered.
    /// </summary>
    public AudioDuckingService(string journalPath)
    {
        _sessions = new((uint)Environment.ProcessId, entries => SaveJournal(journalPath, entries), () => DateTimeOffset.UtcNow, AppDiagnostics.Write);
        Enqueue(() =>
        {
            var previous = AudioDuckingJournal.Load(journalPath);
            if (previous.Count == 0) return;
            lock (_gate) _sessions.Resume(previous);
            RecoverPending();
        });
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

    /// <summary>Completes once queued ducking and restoring has been applied.</summary>
    public Task WhenIdleAsync()
    {
        lock (_queueGate) return _work;
    }

    // Session notifications are only delivered to registrations made on an MTA thread, and the work stays
    // off the dictation path. Requests run in order, so a quick stop never restores before its duck.
    private void Enqueue(Action action)
    {
        lock (_queueGate)
            _work = _work.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.failed", ex); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Duck(string? deviceId, float factor)
    {
        lock (_gate) if (_device is not null) return;
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
        AudioSessionManager manager;
        try { manager = device.AudioSessionManager; }
        catch { device.Dispose(); throw; }
        // Restore releases both, also when listing the sessions fails.
        lock (_gate) (_device, _manager, _starting) = (device, manager, new List<IDuckableAudioSession>());
        manager.OnSessionCreated += OnSessionCreated;
        var sessions = SessionsAfterSubscribing(enumerator, manager, device.ID);
        lock (_gate)
        {
            _sessions.Duck(device.ID, factor, [.. sessions, .. _starting!]);
            _starting = null;
        }
        RecoverPending();
    }

    private void OnSessionCreated(object sender, IAudioSessionControl created)
    {
        try
        {
            if (Session.TryCreate(new AudioSessionControl(created)) is not { } session) return;
            lock (_gate)
            {
                if (sender != _manager) return;
                if (_starting is not null) _starting.Add(session);
                else _sessions.Add(session);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.session-failed", ex); }
    }

    private void Restore()
    {
        MMDevice? device;
        AudioSessionManager? manager;
        lock (_gate)
        {
            _sessions.Restore();
            (device, manager) = (_device, _manager);
            (_device, _manager, _starting) = (null, null, null);
        }
        // Unregister outside the gate: it waits for a running notification, which may be waiting for the gate.
        if (device is not null) Release(device, manager, OnSessionCreated);
        RecoverPending();
    }

    // Restores what an interrupted run left lowered and keeps listening on each output until every such app
    // has played again. An output that is not connected now is retried once Windows reports it active.
    private void RecoverPending()
    {
        List<string> devices;
        var idle = new List<(MMDevice Device, AudioSessionManager Manager)>();
        lock (_gate)
        {
            var pending = _sessions.PendingDevices;
            foreach (var deviceId in _recovery.Keys.Except(pending).ToList())
            {
                idle.Add(_recovery[deviceId]);
                _recovery.Remove(deviceId);
            }
            devices = [.. pending.Except(_recovery.Keys)];
        }
        foreach (var (device, manager) in idle) Release(device, manager, OnRecoverySessionCreated);
        var disconnected = false;
        if (devices.Count > 0)
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var deviceId in devices)
            {
                try { disconnected |= !Listen(enumerator, deviceId); }
                // A removed output is not an error; Windows reports it again once it is added.
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == ElementNotFound) { disconnected = true; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { disconnected = true; AppDiagnostics.Write("audio.ducking.recover-failed", ex); }
            }
        }
        WatchOutputs(disconnected);
    }

    // Returns false when the output is not active.
    private bool Listen(MMDeviceEnumerator enumerator, string deviceId)
    {
        var device = enumerator.GetDevice(deviceId);
        if (device.State != DeviceState.Active) { device.Dispose(); return false; }
        AudioSessionManager manager;
        try { manager = device.AudioSessionManager; }
        catch { device.Dispose(); throw; }
        // Registered before subscribing, so a session reported while the existing ones are listed is not dropped.
        // From here on RecoverPending releases it once nothing is pending for this output.
        lock (_gate) _recovery[deviceId] = (device, manager);
        manager.OnSessionCreated += OnRecoverySessionCreated;
        var sessions = SessionsAfterSubscribing(enumerator, manager, deviceId);
        bool settled;
        lock (_gate)
        {
            _sessions.Recover(deviceId, sessions);
            settled = !_sessions.PendingDevices.Contains(deviceId) && _recovery.Remove(deviceId);
        }
        if (settled) Release(device, manager, OnRecoverySessionCreated);
        return true;
    }

    private void WatchOutputs(bool watch)
    {
        if (watch == _outputWatch is not null) return;
        if (watch)
        {
            var enumerator = new MMDeviceEnumerator();
            var watcher = new OutputWatch(() => Enqueue(RecoverPending));
            if (enumerator.RegisterEndpointNotificationCallback(watcher) < 0) { enumerator.Dispose(); return; }
            _outputWatch = (enumerator, watcher);
        }
        else
        {
            var (enumerator, watcher) = _outputWatch!.Value;
            _outputWatch = null;
            enumerator.UnregisterEndpointNotificationCallback(watcher);
            enumerator.Dispose();
        }
    }

    private void OnRecoverySessionCreated(object sender, IAudioSessionControl created)
    {
        try
        {
            if (Session.TryCreate(new AudioSessionControl(created)) is not { } session) return;
            lock (_gate)
            {
                if (_recovery.FirstOrDefault(listener => listener.Value.Manager == sender).Key is not { } deviceId) return;
                _sessions.Recover(deviceId, [session]);
                // Unregistering from within a notification is not allowed.
                if (!_sessions.PendingDevices.Contains(deviceId)) Enqueue(RecoverPending);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.recover-failed", ex); }
    }

    // Windows reports sessions only once the subscribed manager's list was first counted. Listing through a
    // fresh manager after that leaves no session between the list and the notifications.
    private static List<IDuckableAudioSession> SessionsAfterSubscribing(MMDeviceEnumerator enumerator, AudioSessionManager subscribed, string deviceId)
    {
        _ = subscribed.Sessions.Count;
        using var listing = enumerator.GetDevice(deviceId);
        return Sessions(listing.AudioSessionManager);
    }

    private static void Release(MMDevice device, AudioSessionManager? manager, AudioSessionManager.SessionCreatedDelegate handler)
    {
        if (manager is not null) manager.OnSessionCreated -= handler;
        manager?.Dispose();
        device.Dispose();
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

    private sealed class Session : IDuckableAudioSession
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

        // An expired session belongs to an app that stopped playing; it is no longer in the volume mixer.
        internal static Session? TryCreate(AudioSessionControl control) =>
            control.State == AudioSessionState.AudioSessionStateExpired ? null : new(control);
    }

    private sealed class OutputWatch(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { if (newState == DeviceState.Active) changed(); }
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
