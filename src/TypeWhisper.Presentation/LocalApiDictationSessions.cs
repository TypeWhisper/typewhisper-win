using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>A pollable dictation result associated with one actual capture generation.</summary>
public sealed record LocalApiDictationSession(string Id, long Generation, string Status,
    TranscriptionRecord? Transcription = null, string? Error = null);

/// <summary>Retains bounded API results without attributing later GUI dictations to an earlier request.</summary>
public sealed class LocalApiDictationSessions
{
    private readonly Dictionary<string, LocalApiDictationSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers the actual recording generation, reusing its API identity when stopped.</summary>
    public LocalApiDictationSession Register(long generation)
    {
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        var existing = _sessions.Values.FirstOrDefault(item => item.Generation == generation);
        if (existing is not null) return existing;
        var created = new LocalApiDictationSession(Guid.NewGuid().ToString(), generation, "recording");
        _sessions.Add(created.Id, created);
        while (_sessions.Count > 32) _sessions.Remove(_sessions.Keys.First());
        return created;
    }

    /// <summary>Looks up a UUID in any standard representation.</summary>
    public LocalApiDictationSession? Find(string id) => Guid.TryParse(id, out var parsed)
        ? _sessions.GetValueOrDefault(parsed.ToString()) : null;

    /// <summary>Marks the requested session as processing before asynchronously stopping capture.</summary>
    public void MarkProcessing(string id)
    {
        if (Find(id) is { } current && current.Status == "recording")
            _sessions[current.Id] = current with { Status = "processing" };
    }

    /// <summary>Records a processing failure on the exact requested session.</summary>
    public void Fail(string id)
    {
        if (Find(id) is { } current && current.Status is not ("completed" or "failed"))
            _sessions[current.Id] = current with { Status = "failed", Error = "Dictation processing failed." };
    }

    /// <summary>Updates unfinished sessions using generation-correlated capture and result state.</summary>
    public void Refresh(long generation, bool recording, bool processing, long resultGeneration, TranscriptionRecord? result)
    {
        foreach (var current in _sessions.Values.ToArray())
        {
            if (current.Status is "completed" or "failed") continue;
            if (current.Generation == generation && recording) continue;
            if (current.Generation == generation && processing)
            {
                _sessions[current.Id] = current with { Status = "processing" };
                continue;
            }
            _sessions[current.Id] = result is not null && resultGeneration == current.Generation
                ? current with { Status = "completed", Transcription = result }
                : current with { Status = "failed", Error = "Dictation did not produce a completed transcript." };
        }
    }
}
