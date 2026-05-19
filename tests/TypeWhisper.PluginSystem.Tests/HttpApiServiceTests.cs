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
    public async Task Options_ReturnsNoContentWithoutJsonBody()
    {
        var service = CreateService();

        var response = await service.HandleRequestAsync(new HttpApiRequest(
            "OPTIONS",
            "/v1/models",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None);

        Assert.Equal(204, response.StatusCode);
        Assert.Equal("", response.Body);
        Assert.Equal("text/plain", response.ContentType);
    }

    [Fact]
    public async Task Authentication_ProtectsNonStatusRoutesWhenEnabled()
    {
        var service = CreateService(settings: new AppSettings
        {
            ApiServerRequiresAuthentication = true,
            SaveToHistoryEnabled = true
        });

        var status = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/status",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None);
        var missingToken = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/models",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None);
        var badToken = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/models",
            new NameValueCollection(),
            new Dictionary<string, string> { ["authorization"] = "Bearer wrong-token" },
            []), CancellationToken.None);
        var goodBearer = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/models",
            new NameValueCollection(),
            new Dictionary<string, string> { ["authorization"] = "Bearer test-token" },
            []), CancellationToken.None);
        var goodHeader = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/models",
            new NameValueCollection(),
            new Dictionary<string, string> { ["x-typewhisper-api-token"] = "test-token" },
            []), CancellationToken.None);

        Assert.Equal(200, status.StatusCode);
        Assert.Equal(401, missingToken.StatusCode);
        Assert.Equal("Bearer", missingToken.Headers["WWW-Authenticate"]);
        Assert.Equal(401, badToken.StatusCode);
        Assert.Equal(200, goodBearer.StatusCode);
        Assert.Equal(200, goodHeader.StatusCode);
    }

    [Fact]
    public async Task DictionaryTermsEndpoints_ReplaceMergeAndDeleteSingleTerm()
    {
        var service = CreateService();

        var put = await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/terms",
            """{"terms":[" TypeWhisper ","WhisperKit","typewhisper","Qwen3 "],"replace":true}"""), CancellationToken.None);
        var putJson = JsonObject(put);

        Assert.Equal(200, put.StatusCode);
        Assert.Equal(3, putJson["count"].GetInt32());
        Assert.Equal(["TypeWhisper", "WhisperKit", "Qwen3"], Terms(putJson));

        await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/terms",
            """{"terms":["Raycast","qwen3"],"replace":false}"""), CancellationToken.None);

        var get = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/dictionary/terms",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None));

        Assert.Equal(["qwen3", "Raycast", "TypeWhisper", "WhisperKit"], Terms(get));

        var delete = JsonObject(await service.HandleRequestAsync(JsonRequest(
            "DELETE",
            "/v1/dictionary/terms",
            """{"term":"typewhisper"}"""), CancellationToken.None));

        Assert.True(delete["deleted"].GetBoolean());
        Assert.Equal(3, delete["count"].GetInt32());

        var finalGet = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/dictionary/terms",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None));

        Assert.Equal(["qwen3", "Raycast", "WhisperKit"], Terms(finalGet));
    }

    [Fact]
    public async Task DictionaryCorrectionsEndpoints_ListUpsertDeleteAndValidateInput()
    {
        var service = CreateService();

        var initial = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/dictionary/corrections",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None));

        Assert.Equal(0, initial["count"].GetInt32());
        Assert.Empty(initial["corrections"].EnumerateArray());

        var put = JsonObject(await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/corrections",
            """{"original":"teh","replacement":"the","caseSensitive":false}"""), CancellationToken.None));

        Assert.Equal(1, put["count"].GetInt32());
        var correction = Assert.Single(put["corrections"].EnumerateArray());
        Assert.Equal("teh", correction.GetProperty("original").GetString());
        Assert.Equal("the", correction.GetProperty("replacement").GetString());
        Assert.False(correction.GetProperty("caseSensitive").GetBoolean());

        var upsert = JsonObject(await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/corrections",
            """{"original":"TEH","replacement":"The","caseSensitive":true}"""), CancellationToken.None));

        Assert.Equal(1, upsert["count"].GetInt32());
        correction = Assert.Single(upsert["corrections"].EnumerateArray());
        Assert.Equal("TEH", correction.GetProperty("original").GetString());
        Assert.Equal("The", correction.GetProperty("replacement").GetString());
        Assert.True(correction.GetProperty("caseSensitive").GetBoolean());

        var emptyReplacement = JsonObject(await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/corrections",
            """{"original":"um","replacement":"","caseSensitive":false}"""), CancellationToken.None));
        Assert.Equal(2, emptyReplacement["count"].GetInt32());

        var delete = JsonObject(await service.HandleRequestAsync(JsonRequest(
            "DELETE",
            "/v1/dictionary/corrections",
            """{"original":"teh"}"""), CancellationToken.None));
        Assert.True(delete["deleted"].GetBoolean());
        Assert.Equal(1, delete["count"].GetInt32());

        var missingDelete = JsonObject(await service.HandleRequestAsync(JsonRequest(
            "DELETE",
            "/v1/dictionary/corrections",
            """{"original":"missing"}"""), CancellationToken.None));
        Assert.False(missingDelete["deleted"].GetBoolean());
        Assert.Equal(1, missingDelete["count"].GetInt32());

        var missingOriginalPut = await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/corrections",
            """{"replacement":"value"}"""), CancellationToken.None);
        var missingReplacementPut = await service.HandleRequestAsync(JsonRequest(
            "PUT",
            "/v1/dictionary/corrections",
            """{"original":"value"}"""), CancellationToken.None);
        var missingOriginalDelete = await service.HandleRequestAsync(JsonRequest(
            "DELETE",
            "/v1/dictionary/corrections",
            "{}"), CancellationToken.None);

        Assert.Equal(400, missingOriginalPut.StatusCode);
        Assert.Equal(400, missingReplacementPut.StatusCode);
        Assert.Equal(400, missingOriginalDelete.StatusCode);
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
    public async Task Status_IncludesActiveAccelerationDetails()
    {
        var plugin = new FakeTranscriptionPlugin
        {
            AccelerationStatusOverride = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.NvidiaCuda,
                "Using CUDA",
                "Loaded provider cuda.",
                RequiresRestart: true)
        };
        var (service, modelManager) = CreateServiceWithModelManager(
            new AppSettings
            {
                SelectedModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "tiny"),
                LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda,
                SaveToHistoryEnabled = true
            },
            plugin);

        await modelManager.LoadModelAsync(ModelManagerService.GetPluginModelId(plugin.PluginId, "tiny"));

        var response = await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/status",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None);
        var json = JsonObject(response);
        var acceleration = json["acceleration"];

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, acceleration.GetProperty("preference").GetString());
        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, acceleration.GetProperty("active_backend").GetString());
        Assert.Equal("Using CUDA", acceleration.GetProperty("display_text").GetString());
        Assert.Equal("Loaded provider cuda.", acceleration.GetProperty("detail").GetString());
        Assert.True(acceleration.GetProperty("requires_restart").GetBoolean());
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
    public async Task RulesAndProfilesEndpoints_ListWorkflowBackedRulesAndToggle()
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Docs",
            SortOrder = 7,
            Template = WorkflowTemplate.Summary,
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.App,
                ProcessNames = ["chrome.exe"],
                WebsitePatterns = ["docs.github.com"]
            },
            Behavior = new WorkflowBehavior
            {
                InputLanguage = null,
                Settings = new Dictionary<string, string> { ["inputLanguage"] = """["de","en"]""" },
                TranslationTarget = "fr"
            }
        };
        _workflows.Setup(w => w.Workflows).Returns([workflow]);

        var service = CreateService();
        var rules = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/rules",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None));
        var profiles = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "GET",
            "/v1/profiles",
            new NameValueCollection(),
            new Dictionary<string, string>(),
            []), CancellationToken.None));
        var rule = Assert.Single(rules["rules"].EnumerateArray());
        var profile = Assert.Single(profiles["profiles"].EnumerateArray());

        Assert.Equal("Docs", rule.GetProperty("name").GetString());
        Assert.Equal("multiple", rule.GetProperty("language_mode").GetString());
        Assert.Equal(["de", "en"], rule.GetProperty("language_hints").EnumerateArray().Select(e => e.GetString() ?? "").ToArray());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("input_language").ValueKind);
        Assert.Equal("fr", rule.GetProperty("translation_target_language").GetString());
        Assert.Equal("Docs", profile.GetProperty("name").GetString());

        var toggle = JsonObject(await service.HandleRequestAsync(new HttpApiRequest(
            "PUT",
            "/v1/rules/toggle",
            new NameValueCollection { ["id"] = workflow.Id },
            new Dictionary<string, string>(),
            []), CancellationToken.None));

        Assert.Equal(workflow.Id, toggle["id"].GetString());
        Assert.Equal("Docs", toggle["name"].GetString());
        Assert.Equal("Docs", toggle["rule_name"].GetString());
        Assert.Equal("Docs", toggle["profile_name"].GetString());
        _workflows.Verify(w => w.ToggleWorkflow(workflow.Id), Times.Once);
    }

    [Fact]
    public async Task TranscribeLocalFileEndpoint_RejectsMissingAndUnsupportedFiles()
    {
        var service = CreateService();

        var missing = await service.HandleRequestAsync(JsonRequest(
            "POST",
            "/v1/transcribe/local-file",
            """{"path":"C:\\definitely-missing\\audio.wav"}"""), CancellationToken.None);

        var tempTextFile = Path.GetTempFileName();
        try
        {
            var unsupported = await service.HandleRequestAsync(JsonRequest(
                "POST",
                "/v1/transcribe/local-file",
                JsonSerializer.Serialize(new { path = tempTextFile })), CancellationToken.None);

            Assert.Equal(400, missing.StatusCode);
            Assert.Equal("File not found", ErrorMessage(missing));
            Assert.Equal(400, unsupported.StatusCode);
            Assert.Equal("Unsupported audio format", ErrorMessage(unsupported));
        }
        finally
        {
            File.Delete(tempTextFile);
        }
    }

    private HttpApiService CreateService(params ITranscriptionEnginePlugin[] plugins) =>
        CreateService(null, null, plugins);

    private HttpApiService CreateService(
        AppSettings? settings = null,
        Func<string>? apiTokenProvider = null,
        params ITranscriptionEnginePlugin[] plugins)
    {
        var selectedModel = plugins.Length > 0
            ? ModelManagerService.GetPluginModelId(plugins[0].PluginId, plugins[0].TranscriptionModels[0].Id)
            : null;

        return CreateServiceWithModelManager(settings ?? new AppSettings
        {
            SelectedModelId = selectedModel,
            SaveToHistoryEnabled = true
        }, apiTokenProvider, plugins).Service;
    }

    private (HttpApiService Service, ModelManagerService ModelManager) CreateServiceWithModelManager(
        AppSettings settings,
        params ITranscriptionEnginePlugin[] plugins)
        => CreateServiceWithModelManager(settings, null, plugins);

    private (HttpApiService Service, ModelManagerService ModelManager) CreateServiceWithModelManager(
        AppSettings settings,
        Func<string>? apiTokenProvider,
        params ITranscriptionEnginePlugin[] plugins)
    {
        _settings.Setup(s => s.Current).Returns(settings);

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

        var service = new HttpApiService(
            modelManager,
            _settings.Object,
            new AudioFileService(),
            _history.Object,
            dictionary,
            vocabulary.Object,
            new PostProcessingPipeline(),
            translation.Object,
            null!,
            _workflows.Object,
            apiTokenProvider ?? (() => "test-token"));

        return (service, modelManager);
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

    private static string[] Terms(Dictionary<string, JsonElement> json) =>
        json["terms"].EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

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
        public TranscriptionAccelerationPreference AccelerationPreference { get; private set; } =
            TranscriptionAccelerationPreference.Auto;
        public TranscriptionAccelerationStatus? AccelerationStatusOverride { get; init; }
        public TranscriptionAccelerationStatus AccelerationStatus => AccelerationStatusOverride
            ?? new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Cpu, "Using CPU");

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public void SetAccelerationPreference(TranscriptionAccelerationPreference preference) =>
            AccelerationPreference = preference;

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
