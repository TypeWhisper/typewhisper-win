using System.Text.Json;
using TypeWhisper.Core.Models;
using Xunit;
using Report = TypeWhisper.Presentation.SupportDiagnosticsReport;

namespace TypeWhisper.Presentation.Tests;

public sealed class SupportDiagnosticsReportTests
{
    private static Report Empty() => new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, []);

    [Fact]
    public void WorkflowsKeepTechnicalMetadataWithoutUserContent()
    {
        var workflow = new Workflow
        {
            Id = "private-workflow-id", Name = "private-workflow-name", Template = WorkflowTemplate.Custom,
            Trigger = new()
            {
                Kind = WorkflowTriggerKind.Website, ProcessNames = ["private-app"],
                WebsitePatterns = ["https://private.example"], Hotkeys = ["private-shortcut"]
            },
            Behavior = new()
            {
                ProviderOverride = "com.typewhisper.openai", ModelOverride = "gpt-4.1",
                TranscriptionModelOverride = "C:\\private-model\\weights.bin",
                FineTuning = "private instructions", Settings = new() { ["secret"] = "private-api-key" }
            },
            Output = new() { AutoEnter = true, TargetActionPluginId = "com.typewhisper.webhook", Format = "private-output" }
        };
        var report = Empty() with { Workflows = Report.SummarizeWorkflows([workflow, workflow with { IsEnabled = false }]) };

        var json = report.ToJson();
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var workflows = document.RootElement.GetProperty("workflows");
        Assert.Equal(2, workflows.GetProperty("total").GetInt32());
        Assert.Equal(1, workflows.GetProperty("enabled").GetInt32());
        var metadata = Assert.Single(workflows.GetProperty("enabledWorkflows").EnumerateArray());
        Assert.Equal("Website", metadata.GetProperty("trigger").GetString());
        Assert.Equal("gpt-4.1", metadata.GetProperty("modelId").GetString());
        Assert.Equal(1, metadata.GetProperty("websiteBindingCount").GetInt32());
        Assert.True(metadata.GetProperty("hasCustomInstructions").GetBoolean());
        Assert.True(metadata.GetProperty("autoEnter").GetBoolean());
    }

    [Fact]
    public void WorkflowReportResolvesInheritedProviderWithoutChangingStoredWorkflow()
    {
        var workflow = new Workflow
        {
            Id = "private-id", Name = "private-name", Template = WorkflowTemplate.Summary,
            Trigger = WorkflowTrigger.Manual(), Behavior = new() { ProviderOverride = WorkflowLlmDefaults.Inherit }
        };
        var summary = Report.SummarizeWorkflows([workflow], new("com.typewhisper.openai", "gpt-4.1"));
        var metadata = Assert.Single(summary.EnabledWorkflows);
        Assert.True(metadata.UsesDefaultLlm);
        Assert.Equal("com.typewhisper.openai", metadata.ProviderId);
        Assert.Equal("gpt-4.1", metadata.ModelId);
        Assert.Equal(WorkflowLlmDefaults.Inherit, workflow.Behavior.ProviderOverride);
    }

    [Fact]
    public void UnavailableSectionDoesNotPreventExportOrLeakItsExceptionMessage()
    {
        var capture = new SupportDiagnosticsCapture();
        var workflows = capture.Try<Report.WorkflowInfo>("workflows", () =>
            throw new IOException("Cannot read C:\\Users\\private-user\\private-prompt.json: private-api-key"));
        var api = capture.Try("api", () => new Report.ApiInfo(true, true, 8978, true, true));
        var report = Empty() with { Workflows = workflows, Api = api, CollectionErrors = capture.Errors };

        var json = report.ToJson();
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("api").GetProperty("running").GetBoolean());
        var error = Assert.Single(document.RootElement.GetProperty("collectionErrors").EnumerateArray());
        Assert.Equal("workflows", error.GetProperty("section").GetString());
        Assert.Equal("System.IO.IOException", error.GetProperty("errorType").GetString());
        Assert.False(error.TryGetProperty("message", out _));
    }

    [Theory]
    [InlineData("C:\\Users\\private\\model.gguf", "[omitted]")]
    [InlineData("C:/Users/private/model.gguf", "[omitted]")]
    [InlineData("/home/private/model.gguf", "[omitted]")]
    [InlineData("https://private.example/api?key=secret", "[omitted]")]
    [InlineData("private sentence", "[omitted]")]
    [InlineData("org/model-1.2:latest", "org/model-1.2:latest")]
    [InlineData(null, null)]
    public void IdentifiersExcludePathsUrlsAndFreeText(string? input, string? expected) =>
        Assert.Equal(expected, Report.Identifier(input));

    [Fact]
    public void DisabledLoggingStillExportsReportWithoutRetainedEntries()
    {
        var report = Empty() with
        {
            App = new("1.1.6", "1.1.6+abc", true, 42),
            Log = new(false, 7, [new(DateTimeOffset.UtcNow, "dictation.start")])
        };
        using var document = JsonDocument.Parse(report.ToJson());
        Assert.Equal("1.1.6", document.RootElement.GetProperty("app").GetProperty("version").GetString());
        Assert.Empty(document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public void ExportReappliesLogAdmissionAndWritesValidJson()
    {
        var destination = Path.Combine(Path.GetTempPath(), "typewhisper-support-" + Guid.NewGuid() + ".json");
        try
        {
            var report = Empty() with
            {
                Log = new(true, 7, [new(DateTimeOffset.UtcNow, "dictation.failed", Data: new Dictionary<string, string>
                { ["transcript"] = "private text", ["engine"] = "sherpa-onnx" }, Error: "private exception message")])
            };
            report.Export(destination);
            var json = File.ReadAllText(destination);
            Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            var entry = Assert.Single(document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray());
            Assert.Equal("sherpa-onnx", entry.GetProperty("data").GetProperty("engine").GetString());
            Assert.False(entry.TryGetProperty("error", out _));
        }
        finally { File.Delete(destination); }
    }
}
