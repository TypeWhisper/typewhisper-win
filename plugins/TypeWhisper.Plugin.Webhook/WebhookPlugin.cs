using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Webhook;

public sealed record WebhookConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string HttpMethod { get; init; } = "POST";
    public Dictionary<string, string> Headers { get; init; } = [];
    public bool IsEnabled { get; init; } = true;
    public List<string> ProfileFilter { get; init; } = [];
}

public sealed record DeliveryLogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string WebhookName { get; init; } = "";
    public string Url { get; init; } = "";
    public int? StatusCode { get; init; }
    public string? Error { get; init; }
    public bool Success { get; init; }
}

public sealed class WebhookService
{
    private const int MaxLogEntries = 20;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient = new();
    private readonly IPluginHostServices _host;
    private readonly string _configPath;

    public ObservableCollection<WebhookConfig> Webhooks { get; } = [];
    public ObservableCollection<DeliveryLogEntry> DeliveryLog { get; } = [];

    public WebhookService(IPluginHostServices host)
    {
        _host = host;
        _configPath = Path.Combine(host.PluginDataDirectory, "webhooks.json");
        Load();
    }

    public void AddWebhook(WebhookConfig config)
    {
        Webhooks.Add(config);
        Save();
    }

    public void RemoveWebhook(Guid id)
    {
        var webhook = Webhooks.FirstOrDefault(w => w.Id == id);
        if (webhook is not null)
        {
            Webhooks.Remove(webhook);
            Save();
        }
    }

    public void UpdateWebhook(WebhookConfig updated)
    {
        for (var i = 0; i < Webhooks.Count; i++)
        {
            if (Webhooks[i].Id == updated.Id)
            {
                Webhooks[i] = updated;
                Save();
                return;
            }
        }
    }

    public async Task SendWebhooksAsync(TranscriptionCompletedEvent evt)
    {
        foreach (var webhook in Webhooks.ToList())
        {
            if (!webhook.IsEnabled) continue;

            if (webhook.ProfileFilter.Count > 0
                && (evt.ProfileName is null || !webhook.ProfileFilter.Contains(evt.ProfileName)))
                continue;

            await SendSingleAsync(webhook, evt, retryOnFailure: true);
        }
    }

    private async Task SendSingleAsync(WebhookConfig webhook, TranscriptionCompletedEvent evt, bool retryOnFailure)
    {
        try
        {
            var payload = new
            {
                text = evt.Text,
                detectedLanguage = evt.DetectedLanguage,
                durationSeconds = evt.DurationSeconds,
                modelId = evt.ModelId,
                profileName = evt.ProfileName,
                timestamp = evt.Timestamp
            };

            var json = JsonSerializer.Serialize(payload, s_jsonOptions);
            var method = webhook.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                ? System.Net.Http.HttpMethod.Put
                : System.Net.Http.HttpMethod.Post;

            using var request = new HttpRequestMessage(method, webhook.Url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            foreach (var header in webhook.Headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            var response = await _httpClient.SendAsync(request);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                AddLogEntry(new DeliveryLogEntry
                {
                    WebhookName = webhook.Name,
                    Url = webhook.Url,
                    StatusCode = statusCode,
                    Success = true
                });
            }
            else
            {
                AddLogEntry(new DeliveryLogEntry
                {
                    WebhookName = webhook.Name,
                    Url = webhook.Url,
                    StatusCode = statusCode,
                    Error = $"HTTP {statusCode}",
                    Success = false
                });

                if (retryOnFailure)
                {
                    await Task.Delay(5000);
                    await SendSingleAsync(webhook, evt, retryOnFailure: false);
                }
            }
        }
        catch (Exception ex)
        {
            AddLogEntry(new DeliveryLogEntry
            {
                WebhookName = webhook.Name,
                Url = webhook.Url,
                Error = ex.Message,
                Success = false
            });

            if (retryOnFailure)
            {
                await Task.Delay(5000);
                await SendSingleAsync(webhook, evt, retryOnFailure: false);
            }
        }
    }

    private void AddLogEntry(DeliveryLogEntry entry)
    {
        DeliveryLog.Insert(0, entry);
        while (DeliveryLog.Count > MaxLogEntries)
            DeliveryLog.RemoveAt(DeliveryLog.Count - 1);
    }

    private void Load()
    {
        if (!File.Exists(_configPath)) return;

        try
        {
            var json = File.ReadAllText(_configPath);
            var configs = JsonSerializer.Deserialize<List<WebhookConfig>>(json, s_jsonOptions);
            if (configs is null) return;

            foreach (var config in configs)
                Webhooks.Add(config);
        }
        catch
        {
            _host.Log(PluginLogLevel.Warning, "Failed to load webhook configuration");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var json = JsonSerializer.Serialize(Webhooks.ToList(), s_jsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _host.Log(PluginLogLevel.Warning, $"Failed to save webhook configuration: {ex.Message}");
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class WebhookPlugin : ITypeWhisperPlugin
{
    private IDisposable? _subscription;
    private IPluginHostServices? _host;

    public string PluginId => "com.typewhisper.webhook";
    public string PluginName => "Webhook";
    public string PluginVersion => "2.0.0";

    public WebhookService? Service { get; private set; }

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        Service = new WebhookService(host);
        _subscription = host.EventBus.Subscribe<TranscriptionCompletedEvent>(OnTranscriptionCompleted);
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new WebhookSettingsView(this);

    public IPluginHostServices? Host => _host;

    private Task OnTranscriptionCompleted(TranscriptionCompletedEvent evt)
        => Service?.SendWebhooksAsync(evt) ?? Task.CompletedTask;

    public void Dispose()
    {
        _subscription?.Dispose();
        Service?.Dispose();
    }
}
