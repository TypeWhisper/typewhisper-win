using System.IO;
using System.Text.Json;
using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI;

// One app's audio session on the ducked output.
internal interface IDuckableAudioSession
{
    // Names the app across runs; Windows remembers each app's volume under it.
    string SessionId { get; }
    // Names this running session.
    string InstanceId { get; }
    uint ProcessId { get; }
    float Volume { get; set; }
}

// A lowered session volume. Entries left from an earlier run mean that run ended while ducked.
internal sealed record AudioDuckingJournalEntry(string DeviceId, string SessionId, float Saved, float Ducked, DateTimeOffset At);

internal static class AudioDuckingJournal
{
    internal static IReadOnlyList<AudioDuckingJournalEntry> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<AudioDuckingJournalEntry>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException)
        {
            // The next save would replace it, so it is kept for a look at what was lowered.
            File.Move(path, path + ".corrupt", overwrite: true);
            throw;
        }
    }

    internal static void Save(string path, IReadOnlyList<AudioDuckingJournalEntry> entries)
    {
        if (entries.Count == 0) File.Delete(path);
        else AtomicFileWriter.WriteAllText(path, JsonSerializer.Serialize(entries));
    }
}

// Lowers other apps' sessions instead of the output's master volume, so TypeWhisper's own sounds and spoken
// feedback keep their volume. Windows keeps per-app volumes across restarts, so every lowered volume is
// journaled before it is set; the next start restores apps an interrupted run left lowered.
// A volume that no longer matches the lowered one was changed by the user and is kept.
internal sealed class AudioSessionDucking(uint ownProcessId, Func<IReadOnlyList<AudioDuckingJournalEntry>, bool> saveJournal,
    Func<DateTimeOffset> now, Action<string, Exception> log)
{
    private const float Tolerance = 0.001f;
    private readonly List<AudioDuckingJournalEntry> _pending = [];
    private readonly Dictionary<string, (IDuckableAudioSession Session, AudioDuckingJournalEntry Entry)> _ducked = new(StringComparer.Ordinal);
    private string? _deviceId;
    private float _factor;

    internal bool IsDucked => _deviceId is not null;
    // Sessions kept for restoring; the caller releases every other session it passed in.
    internal bool Holds(IDuckableAudioSession session) => _ducked.Values.Any(ducked => ReferenceEquals(ducked.Session, session));
    internal IReadOnlyList<string> PendingDevices => _pending.Select(entry => entry.DeviceId).Distinct(StringComparer.Ordinal).ToList();

    // Kept until the app plays again, however long that takes, since Windows keeps its lowered volume until then.
    internal void Resume(IEnumerable<AudioDuckingJournalEntry> previous) => _pending.AddRange(previous);

    // Restores sessions an earlier run left lowered. Entries whose app is not playing now stay pending.
    internal void Recover(string deviceId, IReadOnlyList<IDuckableAudioSession> sessions)
    {
        var changed = false;
        foreach (var entry in _pending.Where(entry => entry.DeviceId == deviceId).ToList())
        {
            var matching = sessions.Where(session => session.SessionId == entry.SessionId && session.ProcessId != ownProcessId).ToList();
            if (matching.Count == 0) continue;
            try
            {
                foreach (var session in matching.Where(session => Math.Abs(session.Volume - entry.Ducked) < Tolerance))
                    session.Volume = entry.Saved;
                _pending.Remove(entry);
                changed = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log("audio.ducking.recover-failed", ex); }
        }
        if (changed) Save();
    }

    internal void Duck(string deviceId, float factor, IReadOnlyList<IDuckableAudioSession> sessions)
    {
        if (IsDucked) return;
        _deviceId = deviceId;
        _factor = Math.Clamp(factor, 0f, 1f);
        Recover(deviceId, sessions);
        Lower(sessions);
    }

    // A session that starts playing during the recording is lowered as well.
    internal void Add(IDuckableAudioSession session)
    {
        if (_deviceId is null) return;
        Recover(_deviceId, [session]);
        Lower([session]);
    }

    // Returns the sessions it no longer holds.
    internal IReadOnlyList<IDuckableAudioSession> Restore()
    {
        if (!IsDucked) return [];
        List<IDuckableAudioSession> released = [.. _ducked.Values.Select(ducked => ducked.Session)];
        foreach (var (session, entry) in _ducked.Values)
        {
            try
            {
                if (Math.Abs(session.Volume - entry.Ducked) < Tolerance) session.Volume = entry.Saved;
            }
            // The next start retries once the app plays again.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _pending.Add(entry); log("audio.ducking.restore-failed", ex); }
        }
        _ducked.Clear();
        _deviceId = null;
        Save();
        return released;
    }

    private void Lower(IReadOnlyList<IDuckableAudioSession> sessions)
    {
        var lowered = new List<(IDuckableAudioSession Session, AudioDuckingJournalEntry Entry)>();
        // Checked per session, so a session listed twice is lowered once.
        foreach (var session in sessions.Where(session => session.ProcessId != ownProcessId && !_ducked.ContainsKey(session.InstanceId)))
        {
            try
            {
                var saved = session.Volume;
                // An app that recreates its session during the recording gets the volume already lowered for it.
                var earlier = _ducked.Values.Select(item => item.Entry)
                    .FirstOrDefault(entry => entry.SessionId == session.SessionId && Math.Abs(saved - entry.Ducked) < Tolerance);
                if (earlier is not null)
                {
                    _ducked[session.InstanceId] = (session, earlier);
                    continue;
                }
                var ducked = saved * _factor;
                if (saved - ducked < Tolerance) continue;
                var entry = new AudioDuckingJournalEntry(_deviceId!, session.SessionId, saved, ducked, now());
                _ducked[session.InstanceId] = (session, entry);
                lowered.Add((session, entry));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log("audio.ducking.session-failed", ex); }
        }
        if (lowered.Count == 0) return;
        // Without a journal entry a crash would leave the app lowered, so it is not lowered at all.
        if (!Save())
        {
            foreach (var (session, _) in lowered) _ducked.Remove(session.InstanceId);
            return;
        }
        var failed = false;
        foreach (var (session, entry) in lowered)
        {
            try { session.Volume = entry.Ducked; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _ducked.Remove(session.InstanceId); failed = true; log("audio.ducking.session-failed", ex); }
        }
        if (failed) Save();
    }

    private bool Save() => saveJournal([.. _pending, .. _ducked.Values.Select(ducked => ducked.Entry)]);
}
