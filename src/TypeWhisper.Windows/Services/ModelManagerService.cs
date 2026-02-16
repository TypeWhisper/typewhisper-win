using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Cloud;
using TypeWhisper.Windows.Services.Providers;

namespace TypeWhisper.Windows.Services;

public sealed class ModelManagerService : INotifyPropertyChanged, IDisposable
{
    private readonly Dictionary<string, LocalProviderBase> _localProviders;
    private readonly Dictionary<string, CloudProviderBase> _cloudProviders;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, ModelStatus> _modelStatuses = new();
    private string? _activeModelId;
    private string? _loadedLocalModelId;
    private readonly HttpClient _httpClient = new();
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ActiveModelId
    {
        get => _activeModelId;
        private set { _activeModelId = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<LocalProviderBase> LocalProviders => [.. _localProviders.Values];
    public IReadOnlyList<CloudProviderBase> CloudProviders => [.. _cloudProviders.Values];

    public ITranscriptionEngine Engine
    {
        get
        {
            if (_activeModelId is not null && CloudProvider.IsCloudModel(_activeModelId))
            {
                var (providerId, _) = CloudProvider.ParseCloudModelId(_activeModelId);
                if (_cloudProviders.TryGetValue(providerId, out var cloud))
                    return cloud;
            }

            if (_loadedLocalModelId is not null && _localProviders.TryGetValue(_loadedLocalModelId, out var local))
                return local;

            return _localProviders.Values.First();
        }
    }

    public ModelManagerService(
        IEnumerable<LocalProviderBase> localProviders,
        IEnumerable<CloudProviderBase> cloudProviders,
        ISettingsService settings)
    {
        _localProviders = localProviders.ToDictionary(p => p.Id);
        _cloudProviders = cloudProviders.ToDictionary(p => p.Id);
        _settings = settings;

        foreach (var p in _localProviders.Values)
            _modelStatuses[p.Id] = ModelStatus.NotDownloaded;

        ConfigureProviderKeys();
        settings.SettingsChanged += _ => ConfigureProviderKeys();
    }

    private void ConfigureProviderKeys()
    {
        foreach (var provider in _cloudProviders.Values)
        {
            var encrypted = GetEncryptedKey(provider.Id);
            if (!string.IsNullOrEmpty(encrypted))
            {
                var key = ApiKeyProtection.Decrypt(encrypted);
                if (!string.IsNullOrEmpty(key))
                    provider.Configure(key);
            }
        }
    }

    private string? GetEncryptedKey(string providerId) => providerId switch
    {
        "groq" => _settings.Current.GroqApiKey,
        "openai" => _settings.Current.OpenAiApiKey,
        _ => null
    };

    public ModelStatus GetStatus(string modelId)
    {
        if (CloudProvider.IsCloudModel(modelId))
        {
            if (_activeModelId == modelId) return ModelStatus.Ready;
            var (providerId, _) = CloudProvider.ParseCloudModelId(modelId);
            var provider = _cloudProviders.GetValueOrDefault(providerId);
            return provider is { IsConfigured: true } ? ModelStatus.Ready : ModelStatus.NotDownloaded;
        }
        return _modelStatuses.GetValueOrDefault(modelId, ModelStatus.NotDownloaded);
    }

    public bool IsDownloaded(string modelId)
    {
        if (CloudProvider.IsCloudModel(modelId)) return true;
        return _localProviders.TryGetValue(modelId, out var provider) && provider.IsDownloaded;
    }

    public async Task DownloadAndLoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var provider = _localProviders.GetValueOrDefault(modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");

        if (!provider.IsDownloaded)
            await DownloadModelFilesAsync(provider, cancellationToken);

        await LoadModelAsync(modelId, cancellationToken);
    }

    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (CloudProvider.IsCloudModel(modelId))
        {
            var (providerId, cloudModelId) = CloudProvider.ParseCloudModelId(modelId);
            var provider = _cloudProviders.GetValueOrDefault(providerId)
                ?? throw new ArgumentException($"Unknown provider: {providerId}");
            if (!provider.IsConfigured)
                throw new InvalidOperationException($"Kein API-Key für {provider.DisplayName}");
            provider.SelectTranscriptionModel(cloudModelId);
            ActiveModelId = modelId;
            return;
        }

        var localProvider = _localProviders.GetValueOrDefault(modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");

        if (_loadedLocalModelId == modelId && localProvider.IsModelLoaded)
        {
            ActiveModelId = modelId;
            SetStatus(modelId, ModelStatus.Ready);
            return;
        }

        if (!localProvider.IsDownloaded)
            throw new FileNotFoundException($"Model files not found for: {modelId}");

        // Unload all local providers and force native memory cleanup
        foreach (var p in _localProviders.Values)
            p.UnloadModel();
        _loadedLocalModelId = null;

        if (ActiveModelId is not null)
        {
            ActiveModelId = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        SetStatus(modelId, ModelStatus.LoadingModel);
        try
        {
            var dir = localProvider.GetModelDirectory();
            await localProvider.LoadModelAsync(dir, cancellationToken);

            SetStatus(modelId, ModelStatus.Ready);
            _loadedLocalModelId = modelId;
            ActiveModelId = modelId;
        }
        catch (Exception ex)
        {
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    public void UnloadModel()
    {
        if (ActiveModelId is not null)
        {
            foreach (var p in _localProviders.Values)
                p.UnloadModel();
            _loadedLocalModelId = null;
            SetStatus(ActiveModelId, ModelStatus.NotDownloaded);
            ActiveModelId = null;
        }
    }

    public void DeleteModel(string modelId)
    {
        if (ActiveModelId == modelId)
            UnloadModel();

        var provider = _localProviders.GetValueOrDefault(modelId);
        if (provider is null) return;

        var dir = provider.GetModelDirectory();
        foreach (var file in provider.Model.Files)
        {
            var path = Path.Combine(dir, file.FileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        if (provider.Model.SubDirectory is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);

        SetStatus(modelId, ModelStatus.NotDownloaded);
    }

    private async Task DownloadModelFilesAsync(LocalProviderBase provider, CancellationToken cancellationToken)
    {
        var dir = provider.GetModelDirectory();
        Directory.CreateDirectory(dir);

        var totalBytes = provider.Model.Files.Sum(f => (long)f.EstimatedSizeMB * 1024 * 1024);
        long cumulativeBytesRead = 0;

        foreach (var file in provider.Model.Files)
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
                        SetStatus(provider.Id, ModelStatus.DownloadingModel(progress));
                        lastReport = now;
                    }
                }
            }

            File.Move(filePath + ".tmp", filePath, overwrite: true);
            cumulativeBytesRead += fileBytesRead;
        }
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
        if (!_disposed)
        {
            _httpClient.Dispose();
            foreach (var p in _localProviders.Values)
                p.Dispose();
            foreach (var p in _cloudProviders.Values)
                p.Dispose();
            _disposed = true;
        }
    }
}
