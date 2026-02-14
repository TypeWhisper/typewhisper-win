using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

public sealed class HttpApiService : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public bool IsRunning => _listener?.IsListening == true;

    public HttpApiService(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
    }

    public void Start(int port)
    {
        if (_listener is { IsListening: true }) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* continue listening */ }
        }
    }

    private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            var (statusCode, body) = (path, method) switch
            {
                ("/v1/status", "GET") => HandleStatus(),
                ("/v1/models", "GET") => HandleModels(),
                ("/v1/transcribe", "POST") => await HandleTranscribe(request, ct),
                _ => (404, JsonSerializer.Serialize(new { error = "Not found" }))
            };

            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            response.ContentType = "application/json";
            var errorBytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { error = ex.Message }));
            response.ContentLength64 = errorBytes.Length;
            await response.OutputStream.WriteAsync(errorBytes, ct);
        }
        finally
        {
            response.Close();
        }
    }

    private (int, string) HandleStatus()
    {
        var result = new
        {
            status = _modelManager.Engine.IsModelLoaded ? "ready" : "no_model",
            activeModel = _modelManager.ActiveModelId,
            apiVersion = "1.0"
        };
        return (200, JsonSerializer.Serialize(result));
    }

    private (int, string) HandleModels()
    {
        var models = ModelInfo.AvailableModels.Select(m => new
        {
            id = m.Id,
            name = m.DisplayName,
            size = m.SizeDescription,
            engine = m.Engine.ToString().ToLowerInvariant(),
            downloaded = _modelManager.IsDownloaded(m.Id),
            active = _modelManager.ActiveModelId == m.Id
        });
        return (200, JsonSerializer.Serialize(new { models }));
    }

    private async Task<(int, string)> HandleTranscribe(HttpListenerRequest request, CancellationToken ct)
    {
        if (!_modelManager.Engine.IsModelLoaded)
            return (503, JsonSerializer.Serialize(new { error = "No model loaded" }));

        // Read the audio data from the request body
        var tempPath = Path.Combine(Path.GetTempPath(), $"tw_api_{Guid.NewGuid()}.tmp");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await request.InputStream.CopyToAsync(fs, ct);
            }

            var samples = await _audioFile.LoadAudioAsync(tempPath, ct);

            var s = _settings.Current;
            var language = request.QueryString["language"] ?? (s.Language == "auto" ? null : s.Language);
            var taskStr = request.QueryString["task"] ?? s.TranscriptionTask;
            var task = taskStr == "translate" ? TranscriptionTask.Translate : TranscriptionTask.Transcribe;

            var result = await _modelManager.Engine.TranscribeAsync(samples, language, task, ct);

            var response = new
            {
                text = result.Text,
                language = result.DetectedLanguage,
                duration = result.Duration,
                processing_time = result.ProcessingTime,
                segments = result.Segments.Select(seg => new
                {
                    text = seg.Text,
                    start = seg.Start,
                    end = seg.End
                })
            };

            return (200, JsonSerializer.Serialize(response));
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _cts?.Dispose();
            _disposed = true;
        }
    }
}
