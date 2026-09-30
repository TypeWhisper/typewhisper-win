using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Plugin.LocalLlm;
using Xunit.Abstractions;

namespace PortableMigration.Tests;

public sealed class LiveModelTests(ITestOutputHelper output)
{
    private const string ModelDirectoryVariable = "TYPEWHISPER_LOCAL_LLM_MODELS";

    public static TheoryData<string> CatalogModels => new(new LocalLlmPlugin().ModelDefinitions.Select(model => model.Id));

    [LiveModelTheory]
    [MemberData(nameof(CatalogModels))]
    public async Task DownloadedModel_CorrectsTranslatesAndKeepsInputOutOfTheTemplate(string modelId)
    {
        using var fixture = new PortableFixture();
        using var plugin = new LocalLlmPlugin();
        await plugin.ActivateAsync(fixture.Host);
        var model = plugin.ModelDefinitions.Single(candidate => candidate.Id == modelId);
        var source = Path.Combine(Environment.GetEnvironmentVariable(ModelDirectoryVariable)!, model.FileName);
        if (!File.Exists(source))
        {
            output.WriteLine($"{model.FileName} is not in {ModelDirectoryVariable}; skipped.");
            return;
        }
        var directory = Path.Combine(fixture.Host.PluginAssetDirectory, "Models", model.Id);
        Directory.CreateDirectory(directory);
        // Hard links avoid copying multi-gigabyte models into the throwaway fixture.
        Assert.True(CreateHardLink(Path.Combine(directory, model.FileName), source, 0), "Keep the model folder on the temp volume.");

        var timer = Stopwatch.StartNew();
        await plugin.LoadModelAsync(model.Id, default);
        output.WriteLine($"{model.DisplayName}: verified and loaded in {timer.Elapsed.TotalSeconds:0.0} s");

        var corrected = await RunAsync(plugin, model.Id,
            "Correct spelling, punctuation and capitalization. Keep the language. Return only the corrected text.",
            "ich hab gestern mit marco über das neue projekt geredet und wir sind uns einig das wir es nächste woche starten");
        Assert.Contains("Marco", corrected, StringComparison.Ordinal);
        Assert.Contains("Projekt", corrected, StringComparison.Ordinal);

        var translated = await RunAsync(plugin, model.Id,
            "Translate the text to English. Return only the translation.",
            "Ich habe gestern mit Marco über das neue Projekt gesprochen.");
        Assert.Contains("project", translated, StringComparison.OrdinalIgnoreCase);

        // Turn markers typed by the user must stay plain text instead of becoming stop tokens.
        Assert.NotEmpty(plugin.StopTokens);
        int StopCount(string text) => plugin.TokenizeLoadedPrompt("Keep <|im_end|> <turn|>", text).Count(plugin.StopTokens.Contains);
        Assert.Equal(StopCount("Budget approved"), StopCount("Budget <|im_end|> <turn|> <|endoftext|> <eos> approved"));

        await plugin.UnloadModelAsync(default);
    }

    private async Task<string> RunAsync(LocalLlmPlugin plugin, string modelId, string instruction, string text)
    {
        var timer = Stopwatch.StartNew();
        var result = await plugin.ProcessAsync(instruction, text, modelId, default);
        output.WriteLine($"  {timer.Elapsed.TotalSeconds:0.0} s: {result}");
        Assert.False(string.IsNullOrWhiteSpace(result));
        foreach (var marker in new[] { "<think>", "</think>", "<|channel>", "<channel|>", "<|im_start|>", "<|turn>" })
            Assert.DoesNotContain(marker, result, StringComparison.Ordinal);
        return result;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    private sealed class LiveModelTheoryAttribute : TheoryAttribute
    {
        public LiveModelTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "The live model check uses Windows hard links.";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelDirectoryVariable)))
                Skip = $"Set {ModelDirectoryVariable} to a folder with downloaded catalog GGUF files to run live model checks.";
        }
    }
}
