namespace TypeWhisper.WinUIPrototype;

internal enum PrototypeFileStatus { Queued, Processing, Ready, Canceled, Failed }
internal sealed class PrototypeFileJob(string path, bool fail = false)
{
    internal Guid Id { get; } = Guid.NewGuid();
    internal string Path { get; } = path;
    internal string Name => System.IO.Path.GetFileName(Path);
    internal bool SimulateFailure { get; } = fail;
    internal PrototypeFileStatus Status { get; set; }
    internal int Progress { get; set; }
    internal string Transcript => "DEMO TRANSCRIPT — not generated from your file.\n\n" +
        "We reviewed the launch plan and agreed to keep the first release focused. The next step is to test the complete experience, from choosing a recording to reviewing the finished transcript.\n\n" +
        "Please collect feedback from the team and bring any open questions to our next meeting. Clear decisions and a short list of next steps will help everyone move forward.";
}

// Pure, bounded in-memory queue. The selected media is never opened or uploaded.
internal sealed class PrototypeFileQueue
{
    internal static readonly string[] Extensions = [".wav", ".mp3", ".m4a", ".flac", ".ogg", ".mp4", ".mov", ".webm", ".aac", ".wma", ".mkv"];
    internal List<PrototypeFileJob> Jobs { get; } = [];
    internal bool Running { get; private set; }
    internal string? Add(string path, bool fail = false)
    {
        if (Running) return "Wait for the current run or cancel it before adding files.";
        if (string.IsNullOrWhiteSpace(path) || !Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) return "Unsupported file. Choose an audio or video file.";
        if (Jobs.Any(job => string.Equals(job.Path, path, StringComparison.OrdinalIgnoreCase))) return "This file is already in the queue.";
        if (Jobs.Count >= 20) return "This preview supports up to 20 files at a time.";
        Jobs.Add(new(path, fail)); return null;
    }
    internal bool Start()
    {
        if (Running || !Jobs.Any(job => job.Status == PrototypeFileStatus.Queued)) return false;
        Running = true; return true;
    }
    internal void Tick()
    {
        if (!Running) return;
        var job = Jobs.FirstOrDefault(job => job.Status == PrototypeFileStatus.Processing)
            ?? Jobs.FirstOrDefault(job => job.Status == PrototypeFileStatus.Queued);
        if (job is null) { Running = false; return; }
        job.Status = PrototypeFileStatus.Processing; job.Progress = Math.Min(100, job.Progress + 10);
        if (job.SimulateFailure && job.Progress >= 50) job.Status = PrototypeFileStatus.Failed;
        else if (job.Progress == 100) job.Status = PrototypeFileStatus.Ready;
        Running = Jobs.Any(item => item.Status is PrototypeFileStatus.Queued or PrototypeFileStatus.Processing);
    }
    internal void Cancel()
    {
        Running = false;
        foreach (var job in Jobs.Where(job => job.Status is PrototypeFileStatus.Queued or PrototypeFileStatus.Processing)) job.Status = PrototypeFileStatus.Canceled;
    }
    internal bool Retry(PrototypeFileJob job)
    {
        if (Running || !Jobs.Contains(job) || job.Status is not (PrototypeFileStatus.Canceled or PrototypeFileStatus.Failed)) return false;
        job.Status = PrototypeFileStatus.Queued; job.Progress = 0; return true;
    }
    internal bool Remove(PrototypeFileJob job) => !Running && Jobs.Remove(job);
    internal static string Export(PrototypeFileJob job, string format)
    {
        if (job.Status != PrototypeFileStatus.Ready) throw new InvalidOperationException("The result is not ready.");
        return format switch
        {
            "txt" => job.Transcript,
            "srt" => "1\n00:00:00,000 --> 00:00:05,000\nDEMO — sample text and timestamps, not from your file.\n\n2\n00:00:05,000 --> 00:00:10,000\nWe reviewed the launch plan and agreed on the next steps.\n",
            "vtt" => "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nDEMO — sample text and timestamps, not from your file.\n\n00:00:05.000 --> 00:00:10.000\nWe reviewed the launch plan and agreed on the next steps.\n",
            _ => throw new ArgumentException("Unsupported export format.", nameof(format))
        };
    }
}
