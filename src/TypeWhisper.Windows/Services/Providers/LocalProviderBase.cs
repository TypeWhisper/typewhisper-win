using System.Diagnostics;
using System.IO;
using System.Net.Http;
using SherpaOnnx;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services.Providers;

public abstract class LocalProviderBase : ITranscriptionEngine, IDisposable
{
    private readonly HttpClient _httpClient = new();
    protected readonly object Sync = new();
    protected OfflineRecognizer? Recognizer;
    protected string? ModelDir;
    private bool _disposed;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract ModelInfo Model { get; }

    public bool IsModelLoaded
    {
        get { lock (Sync) return Recognizer is not null; }
    }

    public bool IsDownloaded
    {
        get
        {
            var dir = GetModelDirectory();
            return Model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName)));
        }
    }

    public Task LoadModelAsync(string modelDir, CancellationToken cancellationToken = default)
    {
        UnloadModel();
        return Task.Run(() =>
        {
            lock (Sync)
            {
                Recognizer = CreateRecognizer(modelDir);
                ModelDir = modelDir;
                Debug.WriteLine($"{DisplayName} model loaded from {modelDir}");
            }
        }, cancellationToken);
    }

    public void UnloadModel()
    {
        lock (Sync)
        {
            Recognizer?.Dispose();
            Recognizer = null;
            ModelDir = null;
            OnUnloaded();
        }
    }

    public Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var audioDuration = audioSamples.Length / 16000.0;

            lock (Sync)
            {
                if (Recognizer is null)
                    throw new InvalidOperationException("Kein Modell geladen. LoadModelAsync zuerst aufrufen.");

                OnBeforeTranscribe(language, task);

                using var stream = Recognizer.CreateStream();
                stream.AcceptWaveform(16000, audioSamples);
                Recognizer.Decode(stream);

                var rawText = stream.Result.Text.Trim();
                var (text, detectedLanguage) = ParseResult(rawText);

                sw.Stop();
                var segments = new List<TranscriptionSegment>();
                if (!string.IsNullOrEmpty(text))
                    segments.Add(new TranscriptionSegment(text, 0, audioDuration));

                return new TranscriptionResult
                {
                    Text = text,
                    DetectedLanguage = detectedLanguage,
                    Duration = audioDuration,
                    ProcessingTime = sw.Elapsed.TotalSeconds,
                    Segments = segments
                };
            }
        }, cancellationToken);
    }

    public async Task DownloadModelFilesAsync(CancellationToken cancellationToken = default)
    {
        var dir = GetModelDirectory();
        Directory.CreateDirectory(dir);

        foreach (var file in Model.Files)
        {
            var filePath = Path.Combine(dir, file.FileName);
            if (File.Exists(filePath)) continue;

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tmpPath = filePath + ".tmp";
            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }

            File.Move(tmpPath, filePath, overwrite: true);
        }
    }

    public string GetModelDirectory()
    {
        if (Model.SubDirectory is not null)
            return Path.Combine(TypeWhisperEnvironment.ModelsPath, Model.SubDirectory);
        return TypeWhisperEnvironment.ModelsPath;
    }

    protected abstract OfflineRecognizer CreateRecognizer(string modelDir);
    protected virtual void OnBeforeTranscribe(string? language, TranscriptionTask task) { }
    protected virtual void OnUnloaded() { }
    protected virtual (string Text, string? DetectedLanguage) ParseResult(string rawText) => (rawText, null);

    public void Dispose()
    {
        if (!_disposed)
        {
            UnloadModel();
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
