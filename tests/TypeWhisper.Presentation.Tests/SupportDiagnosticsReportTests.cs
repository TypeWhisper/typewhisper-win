using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;
using Report = TypeWhisper.Presentation.SupportDiagnosticsReport;

namespace TypeWhisper.Presentation.Tests;

public sealed class SupportDiagnosticsReportTests
{
    private static Report Empty() => new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, []);

    [Fact]
    public void PolicySectionKeepsIdentifiersAndOmitsFreeText()
    {
        var reading = new ManagedPolicyReading(true, null, ManagedPolicyRawSection.Of(
            [
                KeyValuePair.Create("TranscriptionProviderId", ManagedPolicyRawValue.FromText("com.typewhisper.sherpa-onnx")),
                KeyValuePair.Create("TranscriptionModelId", ManagedPolicyRawValue.FromText(@"C:\models\private.bin")),
                KeyValuePair.Create("AllowCloudTranscription", ManagedPolicyRawValue.FromNumber(0)),
                KeyValuePair.Create("AllowedPluginsConfigured", ManagedPolicyRawValue.FromNumber(1)),
                KeyValuePair.Create("UpdateMode", ManagedPolicyRawValue.FromNumber(9)),
                KeyValuePair.Create("CustomMarker", ManagedPolicyRawValue.FromNumber(1)),
                KeyValuePair.Create(@"C:\notes\private note", ManagedPolicyRawValue.FromText("x"))
            ],
            [
                KeyValuePair.Create("AllowedPlugins", new ManagedPolicyRawList(["com.typewhisper.sherpa-onnx", "not an id"])),
                KeyValuePair.Create("Customer Secret", new ManagedPolicyRawList(["a"]))
            ]),
            ManagedPolicyRawSection.Missing);
        var snapshot = ManagedPolicyValidator.Validate(reading, 4, DateTimeOffset.UtcNow);

        var policy = Report.SummarizePolicy(snapshot, watching: true);

        Assert.Equal("Present", policy.Status);
        Assert.Equal(4, policy.Revision);
        Assert.True(policy.Watching);
        Assert.Equal(["AllowCloudTranscription", "AllowedPlugins", "TranscriptionModelId", "TranscriptionProviderId"],
            policy.Enforced.Select(value => value.Name));
        Assert.Equal("0", policy.Enforced[0].Value);
        Assert.Equal(["com.typewhisper.sherpa-onnx", "[omitted]"], policy.Enforced[1].Items);
        Assert.Equal("[omitted]", policy.Enforced[2].Value);
        Assert.Equal("com.typewhisper.sherpa-onnx", policy.Enforced[3].Value);
        Assert.Empty(policy.Recommended);
        Assert.Equal(["UpdateMode"], policy.InvalidEnforced);
        var issue = Assert.Single(policy.Issues, item => item.Key == "UpdateMode");
        Assert.Equal("Enforced", issue.Scope);
        // Unknown registry names reach the report only as technical identifiers.
        Assert.Contains(policy.Issues, item => item.Key == "CustomMarker" && item.Message.Contains("CustomMarker"));
        Assert.Equal(2, policy.Issues.Count(item => item.Key == "[omitted]" && item.Message.Contains("[omitted]")));

        var json = (Empty() with { Policy = policy }).ToJson();
        Assert.Contains("\"schemaVersion\": 3", json);
        Assert.Contains("\"policy\"", json);
        Assert.DoesNotContain("private.bin", json);
        Assert.DoesNotContain("not an id", json);
        Assert.DoesNotContain("private note", json);
        Assert.DoesNotContain("Customer Secret", json);
    }

    [Fact]
    public void PolicySectionReportsSensitiveValuesAsSetOnly()
    {
        var reading = new ManagedPolicyReading(true, null, ManagedPolicyRawSection.Of(
            [KeyValuePair.Create("ManagedLicenseKey", ManagedPolicyRawValue.FromText("TW-SECRET-KEY-1234"))]),
            ManagedPolicyRawSection.Missing);

        var policy = Report.SummarizePolicy(ManagedPolicyValidator.Validate(reading, 1, DateTimeOffset.UtcNow), watching: false);

        var value = Assert.Single(policy.Enforced);
        Assert.Equal("ManagedLicenseKey", value.Name);
        Assert.Equal("[set]", value.Value);
        Assert.Null(value.Items);
        Assert.DoesNotContain("SECRET", (Empty() with { Policy = policy }).ToJson());
    }

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
        Assert.Equal("always", metadata.GetProperty("autoEnterMode").GetString());
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
    [InlineData("Users/Alice/private/model.gguf", "[omitted]")]
    [InlineData("models/private.gguf", "[omitted]")]
    [InlineData("models\\private.gguf", "[omitted]")]
    [InlineData("C:Users/Alice/private.gguf", "[omitted]")]
    [InlineData("C:private.gguf", "[omitted]")]
    [InlineData("c:private", "[omitted]")]
    [InlineData("./private", "[omitted]")]
    [InlineData("../private", "[omitted]")]
    [InlineData("https://private.example/api?key=secret", "[omitted]")]
    [InlineData("private sentence", "[omitted]")]
    [InlineData("org/model-1.2:latest", "[omitted]")]
    [InlineData("model-1.2:latest", "model-1.2:latest")]
    [InlineData("com.typewhisper.openai", "com.typewhisper.openai")]
    [InlineData("gpt-4.1", "gpt-4.1")]
    [InlineData(null, null)]
    public void IdentifiersExcludePathsUrlsAndFreeText(string? input, string? expected) =>
        Assert.Equal(expected, Report.Identifier(input));

    [Theory]
    [InlineData("Users/Alice/private/model.gguf")]
    [InlineData("C:Users/Alice/private.gguf")]
    [InlineData("C:private.gguf")]
    public void WorkflowReportOmitsRelativePathsFromOverridesAndDefaults(string path)
    {
        var workflow = new Workflow
        {
            Id = "private-id", Name = "private-name", Template = WorkflowTemplate.Custom,
            Trigger = WorkflowTrigger.Manual(),
            Behavior = new() { ProviderOverride = path, ModelOverride = path, TranscriptionModelOverride = path },
            Output = new() { TargetActionPluginId = path }
        };
        var inherited = workflow with { Behavior = new() { ProviderOverride = WorkflowLlmDefaults.Inherit } };
        var report = Empty() with { Workflows = Report.SummarizeWorkflows([workflow, inherited], new(path, path)) };

        var json = report.ToJson();
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var workflows = document.RootElement.GetProperty("workflows");
        Assert.Equal("[omitted]", workflows.GetProperty("defaultProviderId").GetString());
        Assert.Equal("[omitted]", workflows.GetProperty("defaultModelId").GetString());
        foreach (var metadata in workflows.GetProperty("enabledWorkflows").EnumerateArray())
        {
            Assert.Equal("[omitted]", metadata.GetProperty("providerId").GetString());
            Assert.Equal("[omitted]", metadata.GetProperty("modelId").GetString());
            Assert.Equal("[omitted]", metadata.GetProperty("actionPluginId").GetString());
        }
        Assert.Equal("[omitted]", workflows.GetProperty("enabledWorkflows")[0].GetProperty("transcriptionModelId").GetString());
    }

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
            Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
            var entry = Assert.Single(document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray());
            Assert.Equal("sherpa-onnx", entry.GetProperty("data").GetProperty("engine").GetString());
            Assert.False(entry.TryGetProperty("error", out _));
        }
        finally { File.Delete(destination); }
    }
}
