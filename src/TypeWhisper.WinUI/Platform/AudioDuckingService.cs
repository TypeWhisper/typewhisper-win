using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Lowers other apps on the output while recording; TypeWhisper's own audio keeps its volume.
/// </summary>
public sealed class AudioDuckingService : IAudioDuckingService
{
    private readonly object _gate = new();
    private readonly object _queueGate = new();
    private readonly AudioSessionDucking _sessions;
    private Task _work = Task.CompletedTask;
    // Held while ducked so sessions that start during the recording are reported.
    private MMDevice? _device;
    private AudioSessionManager? _manager;
    // Sessions that started while the existing ones were being listed.
    private List<IDuckableAudioSession>? _starting;

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
        var sessions = Sessions(manager);
        lock (_gate)
        {
            _sessions.Duck(device.ID, factor, [.. sessions, .. _starting!]);
            _starting = null;
        }
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
        if (manager is not null) manager.OnSessionCreated -= OnSessionCreated;
        manager?.Dispose();
        device?.Dispose();
    }

    private void RecoverPending()
    {
        List<string> devices;
        lock (_gate) devices = [.. _sessions.PendingDevices];
        if (devices.Count == 0) return;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var deviceId in devices)
        {
            try
            {
                using var device = enumerator.GetDevice(deviceId);
                if (device.State != DeviceState.Active) continue;
                var sessions = Sessions(device.AudioSessionManager);
                lock (_gate) _sessions.Recover(deviceId, sessions);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.recover-failed", ex); }
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

    private static void SaveJournal(string path, IReadOnlyList<AudioDuckingJournalEntry> entries)
    {
        try { AudioDuckingJournal.Save(path, entries); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("audio.ducking.journal-failed", ex); }
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
}
