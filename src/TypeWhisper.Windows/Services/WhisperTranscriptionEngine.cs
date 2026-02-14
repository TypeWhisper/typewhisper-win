using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Whisper.net;

namespace TypeWhisper.Windows.Services;

public sealed class WhisperTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    private WhisperFactory? _factory;

    public bool IsModelLoaded => _factory is not null;
    public bool IsGpuAccelerated => false;

    public async Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        UnloadModel();

        await Task.Run(() =>
        {
            _factory = WhisperFactory.FromPath(modelPath);
            Debug.WriteLine("Whisper model loaded (CPU)");
        }, cancellationToken);
    }

    public void UnloadModel()
    {
        var old = _factory;
        _factory = null;
        try { old?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"Whisper dispose error (ignored): {ex.Message}"); }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        if (_factory is null)
            throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

        var sw = Stopwatch.StartNew();
        var audioDuration = audioSamples.Length / 16000.0;

        var builder = _factory.CreateBuilder()
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

        if (!string.IsNullOrEmpty(language) && language != "auto")
            builder.WithLanguage(language);
        else
            builder.WithLanguageDetection();

        if (task == TranscriptionTask.Translate)
            builder.WithTranslate();

        using var processor = builder.Build();

        var segments = new List<TranscriptionSegment>();
        string? detectedLanguage = null;

        await foreach (var segment in processor.ProcessAsync(audioSamples, cancellationToken))
        {
            segments.Add(new TranscriptionSegment(
                segment.Text.Trim(),
                segment.Start.TotalSeconds,
                segment.End.TotalSeconds));

            detectedLanguage ??= segment.Language;
        }

        var fullText = string.Join(" ", segments.Select(s => s.Text));
        sw.Stop();

        return new TranscriptionResult
        {
            Text = fullText,
            DetectedLanguage = detectedLanguage,
            Duration = audioDuration,
            ProcessingTime = sw.Elapsed.TotalSeconds,
            Segments = segments
        };
    }

    public void Dispose()
    {
        UnloadModel();
    }
}
