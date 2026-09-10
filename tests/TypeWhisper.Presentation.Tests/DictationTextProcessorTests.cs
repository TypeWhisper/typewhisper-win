using TypeWhisper.Presentation;
using Xunit;

public sealed class DictationTextProcessorTests
{
    [Fact]
    public async Task ProcessorPrioritySharesBuiltInOrderingAndEqualPrioritiesUseIdentity()
    {
        var steps = new List<string>();
        DictationTextProcessor Processor(string id, int priority) => new(id, "1", priority, (text, _) =>
        { steps.Add(id + ":" + text); return Task.FromResult(text + id); });
        var result = await DictationTextPipeline.ProcessAsync("two", new(), "en",
            expandSnippets: (text, _) => { steps.Add("snippet:" + text); return Task.FromResult(text + "S"); },
            textProcessors: [Processor("z", 250), Processor("after", 501), Processor("a", 250)]);
        Assert.Equal(new[] { "a:2", "z:2a", "snippet:2az", "after:2azS" }, steps);
        Assert.Equal("2azSafter", result.Text);
        Assert.Equal(new[] { "a", "z", "after" }, result.TextProcessors.Select(item => item.PluginId));
        Assert.All(result.TextProcessors, item => Assert.Equal("succeeded", item.Status));
    }

    [Fact]
    public async Task FailureRetainsPrecedingTextAndRecordsOnlySanitizedProvenance()
    {
        var result = await DictationTextPipeline.ProcessAsync("two", new(), "en", textProcessors:
        [new("broken", "2.1", 250, (_, _) => throw new IOException("secret transcript"))]);
        Assert.Equal("2", result.Text);
        Assert.DoesNotContain("secret transcript", Assert.Single(result.Warnings));
        var entry = Assert.Single(result.TextProcessors);
        Assert.Equal("broken", entry.PluginId);
        Assert.Equal("2.1", entry.Version);
        Assert.Equal("failed", entry.Status);
    }

    [Fact]
    public async Task CancellationAfterUncooperativeProcessorDoesNotReturnLateText()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = DictationTextPipeline.ProcessAsync("two", new(), "en", ct: cancel.Token, textProcessors:
        [new("slow", "1", 250, async (_, _) => { entered.SetResult(); await release.Task; return "late"; })]);
        await entered.Task;
        cancel.Cancel();
        Assert.False(operation.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }
}
