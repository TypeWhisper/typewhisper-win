using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Moq;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class HttpApiServiceTests : IDisposable
{
    private readonly string _dictionaryPath = Path.GetTempFileName();
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IHistoryService> _history = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();

    public HttpApiServiceTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
        _history.Setup(h => h.Records).Returns([]);
    }

    [Fact]
    public async Task DictionaryTermsEndpoints_SetAppendAndDeleteTerms()
    {
        var service = CreateService();

        var put = await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/terms",
            """{"terms":[" TypeWhisper ","WhisperKit","typewhisper"],"replace":true}"""), CancellationToken.None);
        var putJson = JsonObject(put);

        Assert.Equal(200, put.StatusCode);
        Assert.Equal(2, putJson["count"].GetInt32());

        await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/terms",
            """{"terms":["Kubernetes"],"replace":false}"""), CancellationToken.None);

        var get = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/dictionary/terms",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None));

        var terms = get["terms"].EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["TypeWhisper", "WhisperKit", "Kubernetes"], terms);

        var delete = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "DELETE",
            "/v1/dictionary/terms",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None));

        Assert.True(delete["deleted"].GetBoolean());
        Assert.Equal(0, delete["count"].GetInt32());
    }

    [Fact]
    public async Task TranscribeRejectsLanguageAndLanguageHintsTogether()
    {
        var service = CreateService();
        var request = MultipartTranscribeRequest(
            ("language", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", WavEncoder.Encode([0f, 0f, 0f, 0f])));

        var response = await service.HandleRequestAsync(request, CancellationToken.None);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("language", ErrorMessage(response));
    }

    [Fact]
    public async Task TranscribeRejectsUnknownEngineOverride()
    {
        var service = CreateService(new FakeTranscriptionPlugin());
        var request = MultipartTranscribeRequest(
            ("engine", null, null, "missing"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", WavEncoder.Encode([0f, 0f, 0f, 0f])));

        var response = await service.HandleRequestAsync(request, CancellationToken.None);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Unknown engine", ErrorMessage(response));
    }

    [Fact]
    public async Task TranscribeMultipartRoutesEngineOverrideAndVerboseResponse()
    {
        var plugin = new FakeTranscriptionPlugin();
        var service = CreateService(plugin);
        var wav = WavEncoder.Encode(Enumerable.Repeat(0.05f, 1600).ToArray());
        var request = MultipartTranscribeRequest(
            ("engine", null, null, "mock"u8.ToArray()),
            ("language_hint", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("response_format", null, null, "verbose_json"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", wav));

        var response = await service.HandleRequestAsync(request, CancellationToken.None);
        var json = JsonObject(response);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("mock", json["engine"].GetString());
        Assert.Equal("tiny", json["model"].GetString());
        Assert.Equal("transcribed", json["text"].GetString());
        Assert.Contains("Language hints: de, en", plugin.LastPrompt);
        Assert.Single(json["segments"].EnumerateArray());
    }

    [Fact]
    public async Task HistorySearch_IncludesRaycastCompatibleAliases()
    {
        var record = new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = new DateTime(2026, 4, 23, 10, 15, 0, DateTimeKind.Utc),
            RawText = "raw transcript",
            FinalText = "final transcript",
            AppName = "Notepad",
            AppProcessName = "notepad.exe",
            AppUrl = "https://example.com",
            DurationSeconds = 2.5,
            Language = "en",
            ProfileName = "Writing",
            EngineUsed = "mock",
            ModelUsed = "tiny"
        };
        _history.Setup(h => h.Records).Returns([record]);

        var service = CreateService();
        var response = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/history",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None);
        var json = JsonObject(response);
        var records = json["records"].EnumerateArray().ToList();
        var entries = json["entries"].EnumerateArray().ToList();

        Assert.Equal(200, response.StatusCode);
        Assert.Single(records);
        Assert.Single(entries);
        Assert.Equal("Notepad", entries[0].GetProperty("app_name").GetString());
        Assert.Equal("notepad.exe", entries[0].GetProperty("app_process_name").GetString());
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("app_bundle_id").ValueKind);
        Assert.Equal("https://example.com", entries[0].GetProperty("app_url").GetString());
        Assert.Equal(2, entries[0].GetProperty("words_count").GetInt32());
        Assert.Equal(2, records[0].GetProperty("words").GetInt32());
    }

    [Fact]
    public async Task ProfilesList_ReturnsNotFoundAfterWorkflowMigration()
    {
        var service = CreateService();
        var response = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/profiles",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None);

        Assert.Equal(404, response.StatusCode);
    }

    private HttpApiService CreateService(params ITranscriptionEnginePlugin[] plugins)
    {
        var selectedModel = plugins.Length > 0
            ? ModelManagerService.GetPluginModelId(plugins[0].PluginId, plugins[0].TranscriptionModels[0].Id)
            : null;

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = selectedModel,
            SaveToHistoryEnabled = true
        });

        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            _settings.Object,
            []);
        SetPrivateField(pluginManager, "_transcriptionEngines", plugins.ToList());

        var modelManager = new ModelManagerService(pluginManager, _settings.Object);
        var dictionary = new DictionaryService(_dictionaryPath);
        var vocabulary = new Mock<IVocabularyBoostingService>();
        vocabulary.Setup(v => v.Apply(It.IsAny<string>())).Returns((string text) => text);
        var translation = new Mock<ITranslationService>();
        translation.Setup(t => t.TranslateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, string _, string _, CancellationToken _) => text);

        return new HttpApiService(
            modelManager,
            _settings.Object,
            new AudioFileService(),
            _history.Object,
            dictionary,
            vocabulary.Object,
            new PostProcessingPipeline(),
            translation.Object,
            null!);
    }

    private static HttpApiRequest JsonRequest(string method, string path, string json) =>
        new(
            method,
            path,
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            Encoding.UTF8.GetBytes(json));

    private static HttpApiRequest MultipartTranscribeRequest(
        params (string Name, string? FileName, string? ContentType, byte[] Data)[] parts)
    {
        var boundary = "Boundary-" + Guid.NewGuid().ToString("N");
        using var body = new MemoryStream();
        foreach (var part in parts)
        {
            Write(body, $"--{boundary}\r\n");
            var disposition = $"Content-Disposition: form-data; name=\"{part.Name}\"";
            if (part.FileName is not null)
                disposition += $"; filename=\"{part.FileName}\"";
            Write(body, disposition + "\r\n");
            if (part.ContentType is not null)
                Write(body, $"Content-Type: {part.ContentType}\r\n");
            Write(body, "\r\n");
            body.Write(part.Data);
            Write(body, "\r\n");
        }

        Write(body, $"--{boundary}--\r\n");

        return new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = $"multipart/form-data; boundary={boundary}" },
            body.ToArray());
    }

    private static Dictionary<string, JsonElement> JsonObject(HttpApiResponse response)
    {
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static string ErrorMessage(HttpApiResponse response)
    {
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement
            .GetProperty("error")
            .GetProperty("message")
            .GetString() ?? "";
    }

    private static void Write(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    public void Dispose()
    {
        if (File.Exists(_dictionaryPath))
            File.Delete(_dictionaryPath);
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public string? LastPrompt { get; private set; }

        public string PluginId => "com.typewhisper.mock";
        public string PluginName => "Mock";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "mock";
        public string ProviderDisplayName => "Mock";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("tiny", "Tiny")];
        public string? SelectedModelId { get; private set; } = "tiny";
        public bool SupportsTranslation => true;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) => SelectedModelId = modelId;

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult(new PluginTranscriptionResult("transcribed", language ?? "en", 1.25)
            {
                Segments = [new PluginTranscriptionSegment("transcribed", 0, 1.25)]
            });
        }

        public void Dispose() { }
    }
}
