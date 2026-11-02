using System.Globalization;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryProcessingDetailsTests
{
    [Theory]
    [InlineData("OpenAI", "GPT-4o mini", "OpenAI · GPT-4o mini")]
    [InlineData(" Ollama ", " llama3.2 ", "Ollama · llama3.2")]
    [InlineData(null, "gpt-4o-mini", "gpt-4o-mini")]
    [InlineData("OpenAI", "", "OpenAI")]
    [InlineData(null, null, null)]
    [InlineData(" ", " ", null)]
    public void LlmLabel_ShowsTheRecordedProviderAndModel(string? provider, string? model, string? expected) =>
        Assert.Equal(expected, HistoryWorkspace.LlmLabel(provider, model));

    [Theory]
    [InlineData(0.04, "0.0 s")]
    [InlineData(1.24, "1.2 s")]
    [InlineData(12.36, "12.4 s")]
    [InlineData(59.94, "59.9 s")]
    [InlineData(59.96, "1 min 0 s")]
    [InlineData(125.4, "2 min 5 s")]
    public void ProcessingTime_IsShortAndReadable(double seconds, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, HistoryWorkspace.ProcessingTime(seconds)); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ProcessingTime_IsMissingWhenNotRecorded(double? seconds) => Assert.Null(HistoryWorkspace.ProcessingTime(seconds));

    [Fact]
    public async Task WorkflowLlmUsage_RecordsDisplayNamesAndDuration()
    {
        var clock = new Clock();
        var usage = new WorkflowLlmUsage((provider, model) => (provider == "openai" ? "OpenAI" : "", model == "gpt-4o-mini" ? "GPT-4o mini" : ""), clock);
        string? received = null;
        var process = usage.Wrap((provider, prompt, text, model, ct) =>
        {
            received = $"{provider}|{prompt}|{text}|{model}";
            clock.Milliseconds += 1500;
            return Task.FromResult("Processed.");
        });

        Assert.Null(usage.Provider);
        Assert.Null(usage.Seconds);
        Assert.Equal("Processed.", await process("openai", "Fix it.", "raw", "gpt-4o-mini", CancellationToken.None));

        Assert.Equal("openai|Fix it.|raw|gpt-4o-mini", received);
        Assert.Equal("OpenAI", usage.Provider);
        Assert.Equal("GPT-4o mini", usage.Model);
        Assert.Equal(1.5, usage.Seconds);
    }

    [Fact]
    public async Task WorkflowLlmUsage_FallsBackToIdsAndRecordsNoDurationForAFailedCall()
    {
        var usage = new WorkflowLlmUsage((_, _) => ("", ""), new Clock());
        var process = usage.Wrap((_, _, _, _, _) => Task.FromException<string>(new InvalidOperationException("offline")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => process("custom-provider", "p", "t", "custom-model", CancellationToken.None));

        Assert.Equal("custom-provider", usage.Provider);
        Assert.Equal("custom-model", usage.Model);
        Assert.Null(usage.Seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WorkflowLlmUsage_RecordsNoDurationForAnEmptyAnswerTheWorkflowRejects(string answer)
    {
        var clock = new Clock();
        var usage = new WorkflowLlmUsage((provider, model) => (provider, model), clock);
        var process = usage.Wrap((_, _, _, _, _) => { clock.Milliseconds += 800; return Task.FromResult(answer); });

        Assert.Equal(answer, await process("openai", "p", "t", "gpt-4o-mini", CancellationToken.None));

        Assert.Equal("openai", usage.Provider);
        Assert.Null(usage.Seconds);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
