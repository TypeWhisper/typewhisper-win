using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Webhook;

public sealed class WebhookPlugin : ITypeWhisperPlugin
{
    private readonly HttpClient _httpClient = new();
    private readonly List<IDisposable> _subscriptions = [];
    private IPluginHostServices? _host;

    // Settings
    private string? _webhookUrl;
    private string? _secret;
    private bool _sendRecordingStarted = true;
    private bool _sendRecordingStopped = true;
    private bool _sendTranscriptionCompleted = true;
    private bool _sendTranscriptionFailed = true;
    private bool _sendTextInserted = true;

    public string PluginId => "com.typewhisper.webhook";
    public string PluginName => "Webhook";
    public string PluginVersion => "1.0.0";

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        LoadSettings();

        _subscriptions.Add(host.EventBus.Subscribe<RecordingStartedEvent>(OnRecordingStarted));
        _subscriptions.Add(host.EventBus.Subscribe<RecordingStoppedEvent>(OnRecordingStopped));
        _subscriptions.Add(host.EventBus.Subscribe<TranscriptionCompletedEvent>(OnTranscriptionCompleted));
        _subscriptions.Add(host.EventBus.Subscribe<TranscriptionFailedEvent>(OnTranscriptionFailed));
        _subscriptions.Add(host.EventBus.Subscribe<TextInsertedEvent>(OnTextInserted));

        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => new WebhookSettingsView(this);

    // Properties for settings view binding
    public string WebhookUrl
    {
        get => _webhookUrl ?? "";
        set { _webhookUrl = value; SaveSettings(); }
    }

    public string Secret
    {
        get => _secret ?? "";
        set { _secret = value; SaveSettings(); }
    }

    public bool SendRecordingStarted
    {
        get => _sendRecordingStarted;
        set { _sendRecordingStarted = value; SaveSettings(); }
    }

    public bool SendRecordingStopped
    {
        get => _sendRecordingStopped;
        set { _sendRecordingStopped = value; SaveSettings(); }
    }

    public bool SendTranscriptionCompleted
    {
        get => _sendTranscriptionCompleted;
        set { _sendTranscriptionCompleted = value; SaveSettings(); }
    }

    public bool SendTranscriptionFailed
    {
        get => _sendTranscriptionFailed;
        set { _sendTranscriptionFailed = value; SaveSettings(); }
    }

    public bool SendTextInserted
    {
        get => _sendTextInserted;
        set { _sendTextInserted = value; SaveSettings(); }
    }

    private void LoadSettings()
    {
        _webhookUrl = _host?.GetSetting<string>("webhookUrl");
        _secret = _host?.GetSetting<string>("secret");
        _sendRecordingStarted = _host?.GetSetting<bool?>("sendRecordingStarted") ?? true;
        _sendRecordingStopped = _host?.GetSetting<bool?>("sendRecordingStopped") ?? true;
        _sendTranscriptionCompleted = _host?.GetSetting<bool?>("sendTranscriptionCompleted") ?? true;
        _sendTranscriptionFailed = _host?.GetSetting<bool?>("sendTranscriptionFailed") ?? true;
        _sendTextInserted = _host?.GetSetting<bool?>("sendTextInserted") ?? true;
    }

    private void SaveSettings()
    {
        _host?.SetSetting("webhookUrl", _webhookUrl);
        _host?.SetSetting("secret", _secret);
        _host?.SetSetting("sendRecordingStarted", _sendRecordingStarted);
        _host?.SetSetting("sendRecordingStopped", _sendRecordingStopped);
        _host?.SetSetting("sendTranscriptionCompleted", _sendTranscriptionCompleted);
        _host?.SetSetting("sendTranscriptionFailed", _sendTranscriptionFailed);
        _host?.SetSetting("sendTextInserted", _sendTextInserted);
    }

    // Event handlers
    private Task OnRecordingStarted(RecordingStartedEvent e) =>
        _sendRecordingStarted ? SendWebhookAsync("recording.started", e) : Task.CompletedTask;

    private Task OnRecordingStopped(RecordingStoppedEvent e) =>
        _sendRecordingStopped ? SendWebhookAsync("recording.stopped", e) : Task.CompletedTask;

    private Task OnTranscriptionCompleted(TranscriptionCompletedEvent e) =>
        _sendTranscriptionCompleted ? SendWebhookAsync("transcription.completed", e) : Task.CompletedTask;

    private Task OnTranscriptionFailed(TranscriptionFailedEvent e) =>
        _sendTranscriptionFailed ? SendWebhookAsync("transcription.failed", e) : Task.CompletedTask;

    private Task OnTextInserted(TextInsertedEvent e) =>
        _sendTextInserted ? SendWebhookAsync("text.inserted", e) : Task.CompletedTask;

    private async Task SendWebhookAsync(string eventType, PluginEvent payload)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl)) return;

        try
        {
            var json = JsonSerializer.Serialize(
                new { @event = eventType, data = payload, timestamp = DateTimeOffset.UtcNow },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            using var request = new HttpRequestMessage(HttpMethod.Post, _webhookUrl);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            if (!string.IsNullOrEmpty(_secret))
            {
                var signature = ComputeHmacSha256(json, _secret);
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
            }

            await _httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Webhook failed: {ex.Message}");
        }
    }

    private static string ComputeHmacSha256(string data, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexStringLower(hash);
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        _httpClient.Dispose();
    }
}
