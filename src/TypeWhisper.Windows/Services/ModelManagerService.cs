using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

public sealed class ModelManagerService : INotifyPropertyChanged, IDisposable
{
    private readonly ParakeetTranscriptionEngine _parakeetEngine;
    private readonly Dictionary<string, ModelStatus> _modelStatuses = new();
    private string? _activeModelId;
    private readonly HttpClient _httpClient = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ActiveModelId
    {
        get => _activeModelId;
        private set { _activeModelId = value; OnPropertyChanged(); }
    }

    public ITranscriptionEngine Engine => _parakeetEngine;

    public ModelManagerService(ParakeetTranscriptionEngine parakeetEngine)
    {
        _parakeetEngine = parakeetEngine;

        foreach (var model in ModelInfo.AvailableModels)
        {
            _modelStatuses[model.Id] = ModelStatus.NotDownloaded;
        }
    }

    public ModelStatus GetStatus(string modelId) =>
        _modelStatuses.TryGetValue(modelId, out var status) ? status : ModelStatus.NotDownloaded;

    public bool IsDownloaded(string modelId)
    {
        var model = ModelInfo.GetById(modelId);
        if (model is null) return false;

        var dir = GetModelDirectory(model);
        return model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName)));
    }

    public async Task DownloadAndLoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = ModelInfo.GetById(modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");

        // Download missing files
        if (!IsDownloaded(modelId))
        {
            await DownloadModelFilesAsync(model, cancellationToken);
        }

        // Load
        await LoadEngineAsync(model, cancellationToken);
    }

    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = ModelInfo.GetById(modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");

        if (!IsDownloaded(modelId))
            throw new FileNotFoundException($"Model files not found for: {modelId}");

        await LoadEngineAsync(model, cancellationToken);
    }

    public void UnloadModel()
    {
        if (ActiveModelId is not null)
        {
            _parakeetEngine.UnloadModel();
            SetStatus(ActiveModelId, ModelStatus.NotDownloaded);
            ActiveModelId = null;
        }
    }

    public void DeleteModel(string modelId)
    {
        if (ActiveModelId == modelId)
            UnloadModel();

        var model = ModelInfo.GetById(modelId);
        if (model is null) return;

        var dir = GetModelDirectory(model);
        foreach (var file in model.Files)
        {
            var path = Path.Combine(dir, file.FileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        // Remove empty subdirectory
        if (model.SubDirectory is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);

        SetStatus(modelId, ModelStatus.NotDownloaded);
    }

    private async Task LoadEngineAsync(ModelInfo model, CancellationToken cancellationToken)
    {
        // Unload engine and force native memory cleanup
        _parakeetEngine.UnloadModel();
        if (ActiveModelId is not null)
        {
            ActiveModelId = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        SetStatus(model.Id, ModelStatus.LoadingModel);
        try
        {
            var dir = GetModelDirectory(model);
            await _parakeetEngine.LoadModelAsync(dir, cancellationToken);

            SetStatus(model.Id, ModelStatus.Ready);
            ActiveModelId = model.Id;
        }
        catch (Exception ex)
        {
            SetStatus(model.Id, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    private async Task DownloadModelFilesAsync(ModelInfo model, CancellationToken cancellationToken)
    {
        var dir = GetModelDirectory(model);
        Directory.CreateDirectory(dir);

        var totalBytes = model.Files.Sum(f => (long)f.EstimatedSizeMB * 1024 * 1024);
        long cumulativeBytesRead = 0;

        foreach (var file in model.Files)
        {
            var filePath = Path.Combine(dir, file.FileName);
            if (File.Exists(filePath)) continue;

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var buffer = new byte[81920];
            long fileBytesRead = 0;
            var lastReport = DateTime.UtcNow;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var fileStream = new FileStream(filePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                int read;
                while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    fileBytesRead += read;

                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds > 250 && totalBytes > 0)
                    {
                        var progress = (double)(cumulativeBytesRead + fileBytesRead) / totalBytes;
                        SetStatus(model.Id, ModelStatus.DownloadingModel(progress));
                        lastReport = now;
                    }
                }
            }

            File.Move(filePath + ".tmp", filePath, overwrite: true);
            cumulativeBytesRead += fileBytesRead;
        }
    }

    private static string GetModelDirectory(ModelInfo model)
    {
        if (model.SubDirectory is not null)
            return Path.Combine(TypeWhisperEnvironment.ModelsPath, model.SubDirectory);
        return TypeWhisperEnvironment.ModelsPath;
    }

    private void SetStatus(string modelId, ModelStatus status)
    {
        _modelStatuses[modelId] = status;
        OnPropertyChanged(nameof(GetStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _httpClient.Dispose();
        _parakeetEngine.Dispose();
    }
}
