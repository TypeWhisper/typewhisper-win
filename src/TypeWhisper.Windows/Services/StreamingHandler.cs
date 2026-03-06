using System.Diagnostics;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Polls the growing audio buffer during recording, transcribes periodically,
/// and stabilizes partial text to prevent flickering. Ported from Mac StreamingHandler.swift.
/// </summary>
public sealed class StreamingHandler : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly AudioRecordingService _audio;
    private readonly IDictionaryService _dictionary;

    private CancellationTokenSource? _cts;
    private Task? _streamingTask;
    private string _confirmedText = "";

    public Action<string>? OnPartialTextUpdate { get; set; }

    public StreamingHandler(
        ModelManagerService modelManager,
        AudioRecordingService audio,
        IDictionaryService dictionary)
    {
        _modelManager = modelManager;
        _audio = audio;
        _dictionary = dictionary;
    }

    public void Start(
        string? language,
        TranscriptionTask task,
        Func<bool> isStillRecording)
    {
        Stop();

        _confirmedText = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var engine = _modelManager.Engine;
        var pollInterval = TimeSpan.FromSeconds(3.0);

        _streamingTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(pollInterval, ct);

                while (!ct.IsCancellationRequested && isStillRecording())
                {
                    var buffer = _audio.GetCurrentBuffer();
                    var bufferDuration = buffer is not null ? buffer.Length / 16000.0 : 0;

                    if (buffer is not null && bufferDuration > 0.5)
                    {
                        try
                        {
                            var lang = language == "auto" ? null : language;
                            var result = await engine.TranscribeAsync(buffer, lang, task, ct);
                            var text = result.Text?.Trim() ?? "";

                            if (!string.IsNullOrEmpty(text))
                            {
                                text = _dictionary.ApplyCorrections(text);
                                var stable = StabilizeText(_confirmedText, text);
                                _confirmedText = stable;
                                OnPartialTextUpdate?.Invoke(stable);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Streaming transcription error (non-fatal): {ex.Message}");
                        }
                    }

                    await Task.Delay(pollInterval, ct);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _streamingTask = null;
        _confirmedText = "";
    }

    /// <summary>
    /// Keeps confirmed text stable and only appends new content.
    /// Ported from Mac StreamingHandler.swift stabilizeText().
    /// </summary>
    public static string StabilizeText(string confirmed, string newText)
    {
        newText = newText.Trim();
        if (string.IsNullOrEmpty(confirmed)) return newText;
        if (string.IsNullOrEmpty(newText)) return confirmed;

        // Best case: new text starts with confirmed text
        if (newText.StartsWith(confirmed, StringComparison.Ordinal))
            return newText;

        // Find how far the texts match from the start
        var matchEnd = 0;
        var minLen = Math.Min(confirmed.Length, newText.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (confirmed[i] == newText[i])
                matchEnd = i + 1;
            else
                break;
        }

        // If more than half matches, keep confirmed and append the new tail
        if (matchEnd > confirmed.Length / 2)
            return confirmed + newText[matchEnd..];

        // Suffix-prefix overlap: new text starts with a suffix of confirmed
        var minOverlap = Math.Min(20, confirmed.Length / 4);
        var maxShift = Math.Min(confirmed.Length - minOverlap, 150);
        if (maxShift > 0)
        {
            for (var dropCount = 1; dropCount <= maxShift; dropCount++)
            {
                var suffix = confirmed[dropCount..];
                if (newText.StartsWith(suffix, StringComparison.Ordinal))
                {
                    var newTail = newText[(confirmed.Length - dropCount)..];
                    return string.IsNullOrEmpty(newTail) ? confirmed : confirmed + newTail;
                }
            }
        }

        // Very different result — accept new text to avoid freezing the preview
        return newText;
    }

    public void Dispose()
    {
        Stop();
    }
}
