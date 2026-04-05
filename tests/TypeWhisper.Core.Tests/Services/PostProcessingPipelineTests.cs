using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class PostProcessingPipelineTests
{
    private readonly PostProcessingPipeline _sut = new();

    [Fact]
    public async Task ProcessAsync_NoOptions_ReturnsRawText()
    {
        var result = await _sut.ProcessAsync("hello world", new PipelineOptions());
        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_DictionaryCorrections_Applied()
    {
        var options = new PipelineOptions
        {
            DictionaryCorrector = text => text.Replace("teh", "the")
        };

        var result = await _sut.ProcessAsync("teh quick fox", options);
        Assert.Equal("the quick fox", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_SnippetExpansion_Applied()
    {
        var options = new PipelineOptions
        {
            SnippetExpander = text => text.Replace("brb", "be right back")
        };

        var result = await _sut.ProcessAsync("brb", options);
        Assert.Equal("be right back", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_LlmHandler_Applied()
    {
        var options = new PipelineOptions
        {
            LlmHandler = (text, _) => Task.FromResult(text.ToUpperInvariant())
        };

        var result = await _sut.ProcessAsync("hello", options);
        Assert.Equal("HELLO", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_Translation_Applied()
    {
        var options = new PipelineOptions
        {
            TranslationHandler = (text, src, tgt, _) => Task.FromResult($"[{tgt}] {text}"),
            TranslationTarget = "fr",
            DetectedLanguage = "en"
        };

        var result = await _sut.ProcessAsync("hello", options);
        Assert.Equal("[fr] hello", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_Translation_SkippedWhenEffectiveLanguageMatchesTarget()
    {
        var options = new PipelineOptions
        {
            TranslationHandler = (text, src, tgt, _) => Task.FromResult($"[{tgt}] {text}"),
            TranslationTarget = "it",
            EffectiveSourceLanguage = "it",
            DetectedLanguage = "en" // Whisper hallucinated English
        };

        var result = await _sut.ProcessAsync("ciao mondo", options);
        Assert.Equal("ciao mondo", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_Translation_SkippedWhenSameLanguage()
    {
        var options = new PipelineOptions
        {
            TranslationHandler = (text, src, tgt, _) => Task.FromResult($"[{tgt}] {text}"),
            TranslationTarget = "en",
            DetectedLanguage = "en"
        };

        var result = await _sut.ProcessAsync("hello", options);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_PriorityOrdering_PluginsBeforeLlm()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(100, (text, _) =>
                {
                    executionOrder.Add("Plugin100");
                    return Task.FromResult(text + "+P100");
                })
            ],
            LlmHandler = (text, _) =>
            {
                executionOrder.Add("LLM");
                return Task.FromResult(text + "+LLM");
            },
            SnippetExpander = text =>
            {
                executionOrder.Add("Snippets");
                return text + "+SNP";
            },
            DictionaryCorrector = text =>
            {
                executionOrder.Add("Dictionary");
                return text + "+DICT";
            }
        };

        var result = await _sut.ProcessAsync("start", options);

        // Priority order: Plugin(100) → LLM(300) → Snippets(500) → Dictionary(600)
        Assert.Equal(["Plugin100", "LLM", "Snippets", "Dictionary"], executionOrder);
        Assert.Equal("start+P100+LLM+SNP+DICT", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_MultiplePlugins_SortedByPriority()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(700, (text, _) =>
                {
                    executionOrder.Add("Plugin700");
                    return Task.FromResult(text + "+P700");
                }),
                new PluginPostProcessor(50, (text, _) =>
                {
                    executionOrder.Add("Plugin50");
                    return Task.FromResult(text + "+P50");
                }),
                new PluginPostProcessor(400, (text, _) =>
                {
                    executionOrder.Add("Plugin400");
                    return Task.FromResult(text + "+P400");
                })
            ]
        };

        var result = await _sut.ProcessAsync("start", options);

        // Plugin50(50) → Plugin400(400) → Plugin700(700)
        Assert.Equal(["Plugin50", "Plugin400", "Plugin700"], executionOrder);
        Assert.Equal("start+P50+P400+P700", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_PluginBetweenLlmAndSnippets()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(400, (text, _) =>
                {
                    executionOrder.Add("Plugin400");
                    return Task.FromResult(text);
                })
            ],
            LlmHandler = (text, _) =>
            {
                executionOrder.Add("LLM");
                return Task.FromResult(text);
            },
            SnippetExpander = text =>
            {
                executionOrder.Add("Snippets");
                return text;
            }
        };

        await _sut.ProcessAsync("test", options);

        // LLM(300) → Plugin(400) → Snippets(500)
        Assert.Equal(["LLM", "Plugin400", "Snippets"], executionOrder);
    }

    [Fact]
    public async Task ProcessAsync_ErrorResilience_ContinuesAfterFailure()
    {
        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(100, (_, _) =>
                    throw new InvalidOperationException("Plugin failed"))
            ],
            DictionaryCorrector = text => text + "+DICT"
        };

        var result = await _sut.ProcessAsync("hello", options);

        // Plugin failed but dictionary still applied
        Assert.Equal("hello+DICT", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var options = new PipelineOptions
        {
            DictionaryCorrector = text => text
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.ProcessAsync("test", options, cts.Token));
    }

    [Fact]
    public async Task ProcessAsync_StatusCallback_CalledForLlmAndTranslation()
    {
        var statusCalls = new List<string>();

        var options = new PipelineOptions
        {
            LlmHandler = (text, _) => Task.FromResult(text),
            TranslationHandler = (text, _, _, _) => Task.FromResult(text),
            TranslationTarget = "fr",
            DetectedLanguage = "en",
            StatusCallback = status =>
            {
                statusCalls.Add(status);
                return Task.CompletedTask;
            }
        };

        await _sut.ProcessAsync("test", options);

        Assert.Contains("AI", statusCalls);
        Assert.Contains("Translation", statusCalls);
    }

    [Fact]
    public async Task ProcessAsync_TranslationAlwaysLast()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            DictionaryCorrector = text =>
            {
                executionOrder.Add("Dictionary");
                return text;
            },
            TranslationHandler = (text, _, _, _) =>
            {
                executionOrder.Add("Translation");
                return Task.FromResult(text);
            },
            TranslationTarget = "fr",
            DetectedLanguage = "en"
        };

        await _sut.ProcessAsync("test", options);

        Assert.Equal("Dictionary", executionOrder[0]);
        Assert.Equal("Translation", executionOrder[1]);
    }
}
