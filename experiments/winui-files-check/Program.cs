using TypeWhisper.WinUI;
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
var queue = new FileQueue();
Check(!queue.Start(), "Empty queue cannot run");
Check(queue.Add("invalid.txt") is not null && queue.Jobs.Count == 0, "Unsupported file rejected");
Check(queue.Add("meeting.WAV") is null, "Audio extension is case insensitive");
Check(queue.Add("meeting.wav") is not null, "Duplicate rejected");
Check(queue.Add("interview.mp4") is null, "Video accepted");
Check(queue.Start() && !queue.Start(), "Only one run starts");
Check(queue.Add("extra.wav") is not null, "Adding while running rejected");
queue.Tick(); var first = queue.Jobs[0];
Check(first.Status == FileStatus.Processing && first.Progress == 10, "Progress starts");
Check(!queue.Remove(first), "Running job cannot be removed");
queue.Cancel(); Check(queue.Jobs.All(job => job.Status == FileStatus.Canceled), "Cancel includes queued jobs");
queue.Tick(); Check(first.Progress == 10, "Canceled timer does not advance");
Check(queue.Retry(first) && first.Progress == 0, "Retry resets progress");
queue.Start(); for (var i = 0; i < 10; i++) queue.Tick();
Check(first.Status == FileStatus.Ready && !queue.Running, "Successful completion stops run");
Check(FileQueue.Export(first, "txt").StartsWith("DEMO"), "Text export labeled demo");
Check(FileQueue.Export(first, "srt").Contains("00:00:00,000 --> 00:00:05,000"), "SRT timing syntax");
Check(FileQueue.Export(first, "vtt").StartsWith("WEBVTT\n"), "WebVTT header");
var rejected = false; try { FileQueue.Export(queue.Jobs[1], "txt"); } catch (InvalidOperationException) { rejected = true; }
Check(rejected, "Unfinished export blocked");
rejected = false; try { FileQueue.Export(first, "bad"); } catch (ArgumentException) { rejected = true; }
Check(rejected, "Unknown export format blocked");
queue.Add("failure.wav", true); queue.Start(); for (var i = 0; i < 5; i++) queue.Tick();
Check(queue.Jobs[^1].Status == FileStatus.Failed && !queue.Running, "Simulated failure stops cleanly");
Check(first.Status == FileStatus.Ready, "Existing result preserved after failure");
Check(queue.Retry(queue.Jobs[^1]), "Failed job can retry");
Check(queue.Remove(queue.Jobs[^1]), "Idle item removal allowed");
var bounded = new FileQueue(); for (var i = 0; i < 20; i++) bounded.Add($"{i}.wav");
Check(bounded.Add("overflow.wav") is not null && bounded.Jobs.Count == 20, "Queue limit enforced");
Console.WriteLine($"{count} checks passed.");
