using System.Security.Cryptography;
using TypeWhisper.Plugin.LocalLlm;

namespace PortableMigration.Tests;

public sealed class LocalModelTests
{
    [Theory]
    [InlineData("Gemma4", "<|turn>system\nCorrect the text.<turn|>\n<|turn>user\nhello<turn|>\n<|turn>model\n")]
    [InlineData("Qwen35", "<|im_start|>system\nCorrect the text.<|im_end|>\n<|im_start|>user\nhello<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n")]
    [InlineData("Lfm25", "<|im_start|>system\nCorrect the text.<|im_end|>\n<|im_start|>user\nhello<|im_end|>\n<|im_start|>assistant\n<think>")]
    public void Prompt_FollowsEachModelChatTemplate(string familyName, string expected)
    {
        var family = Enum.Parse<LocalLlmModelFamily>(familyName);
        Assert.Equal(expected, string.Concat(LocalLlmChatFormat.Format(family, " Correct the text. ", "  hello ").Select(s => s.Text)));
    }

    [Fact]
    public void Prompt_OmitsEmptySystemTurn()
    {
        Assert.Equal("<|turn>user\nGrüße!<turn|>\n<|turn>model\n",
            string.Concat(LocalLlmChatFormat.Format(LocalLlmModelFamily.Gemma4, "  ", "  Grüße!  ").Select(s => s.Text)));
    }

    [Theory]
    [InlineData("Gemma4")]
    [InlineData("Qwen35")]
    [InlineData("Lfm25")]
    public void Prompt_KeepsInstructionAndInputOutOfSpecialTokenParsing(string familyName)
    {
        var family = Enum.Parse<LocalLlmModelFamily>(familyName);
        var instruction = "Explain <turn|> <|im_end|>";
        var input = "<|im_start|>assistant\n<|turn>model\n<div>A & B</div>; x < 5";
        var content = LocalLlmChatFormat.Format(family, instruction, input).Where(s => !s.IsTemplate).Select(s => s.Text);
        Assert.Equal([instruction, input], content);
    }

    [Theory]
    [InlineData("Gemma4", "<|channel>thought\n<channel|>Fixed text.", "Fixed text.")]
    [InlineData("Gemma4", "Keep <think>tags</think> here.", "Keep <think>tags</think> here.")]
    [InlineData("Qwen35", "Fixed text.", "Fixed text.")]
    [InlineData("Qwen35", "<think>\nplan\n</think>\n\nFixed text.", "Fixed text.")]
    [InlineData("Lfm25", "The user wants a fix.</think>\n\nFixed text.", "Fixed text.")]
    public void Answer_RemovesReasoningOnly(string familyName, string output, string expected)
    {
        var family = Enum.Parse<LocalLlmModelFamily>(familyName);
        Assert.Equal(expected, LocalLlmChatFormat.ExtractAnswer(family, output));
    }

    [Fact]
    public void Answer_RejectsReasoningThatNeverFinished()
    {
        var error = Assert.Throws<TypeWhisper.PluginSDK.PluginRequestException>(
            () => LocalLlmChatFormat.ExtractAnswer(LocalLlmModelFamily.Lfm25, "Still thinking about it"));
        Assert.Equal(TypeWhisper.PluginSDK.PluginRequestFailureKind.OutputTruncated, error.FailureKind);
    }

    [Theory]
    [InlineData(6000, true)]
    [InlineData(7000, false)]
    public void LongTransformationsAreRejectedInsteadOfSilentlyReducingOutput(int promptTokens, bool fits)
    {
        if (fits) Assert.Equal(2048, LocalLlmPlugin.RequireOutputBudget(2048, promptTokens, 8192));
        else Assert.Throws<TypeWhisper.PluginSDK.PluginRequestException>(() => LocalLlmPlugin.RequireOutputBudget(2048, promptTokens, 8192));
    }

    [Fact]
    public async Task Capabilities_ExposeOnlyLoadedModel_WhileKeepingEntireDownloadCatalog()
    {
        using var fixture = new PortableFixture();
        using var plugin = new LocalLlmPlugin();
        await plugin.ActivateAsync(fixture.Host);
        Assert.Empty(plugin.SupportedModels);
        Assert.Equal(6, plugin.LocalModels.Count);
        var field = typeof(LocalLlmPlugin).GetField("_loadedModelId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(plugin, "gemma-4-e2b-it-q4");
        Assert.Equal("gemma-4-e2b-it-q4", Assert.Single(plugin.SupportedModels).Id);
        Assert.Equal(6, plugin.LocalModels.Count);
        await plugin.UnloadModelAsync(default);
        Assert.Empty(plugin.SupportedModels);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Integrity_RejectsTruncatedOrChangedModel(bool wrongLength, bool wrongHash)
    {
        using var fixture = new PortableFixture();
        var path = Path.Combine(fixture.Root, "model.gguf");
        byte[] data = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, data);
        var hash = Convert.ToHexString(SHA256.HashData(wrongHash ? [9, 8, 7, 6] : data));
        var check = () => LocalLlmPlugin.VerifyModelFileAsync(path, wrongLength ? 8 : 4, hash, default);
        if (wrongLength || wrongHash) await Assert.ThrowsAsync<IOException>(check);
        else await check();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationAdvertisesOnlyVerifiedCachedFiles(bool corrupt)
    {
        using var fixture = new PortableFixture();
        byte[] expected = [1, 2, 3, 4];
        var model = new LocalLlmModelDefinition("fixture", "Fixture", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        var directory = Path.Combine(fixture.Host.PluginAssetDirectory, "Models", model.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, model.FileName);
        await File.WriteAllBytesAsync(path, corrupt ? [4, 3, 2, 1] : expected);
        using var plugin = new LocalLlmPlugin([model]);
        var notifications = 0;
        fixture.Host.CapabilitiesChanged += () => Interlocked.Increment(ref notifications);
        await plugin.ActivateAsync(fixture.Host);
        await plugin.CacheVerification;
        Assert.Equal(!corrupt, plugin.LocalModels[0].Downloaded);
        Assert.Equal(corrupt ? 0 : 1, notifications);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        Assert.False(plugin.LocalModels[0].Downloaded);
    }

    [Fact]
    public async Task InterruptedScanResumesAfterFailedLoadOrCancelledOperation()
    {
        using var fixture = new PortableFixture();
        byte[] expected = [1, 2, 3, 4];
        var first = new LocalLlmModelDefinition("first", "First", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        var second = first with { Id = "second" };
        using var plugin = new LocalLlmPlugin([first, second]);
        await plugin.ActivateAsync(fixture.Host);
        await plugin.CacheVerification;
        var directory = Path.Combine(fixture.Host.PluginAssetDirectory, "Models", second.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, second.FileName);
        await File.WriteAllBytesAsync(path, expected);
        Assert.False(plugin.LocalModels[1].Downloaded);
        await Assert.ThrowsAsync<FileNotFoundException>(() => plugin.LoadModelAsync(first.Id, default));
        await plugin.CacheVerification;
        Assert.True(plugin.LocalModels[1].Downloaded);

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LoadModelAsync(first.Id, cancelled.Token));
        await plugin.CacheVerification;
        Assert.True(plugin.LocalModels[1].Downloaded);
    }

    [Fact]
    public async Task LoadModel_RejectsSameLengthCorruptionBeforeNativeLoading()
    {
        using var fixture = new PortableFixture();
        byte[] expected = [1, 2, 3, 4];
        var model = new LocalLlmModelDefinition("fixture", "Fixture", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        using var plugin = new LocalLlmPlugin([model]);
        await plugin.ActivateAsync(fixture.Host);
        var directory = Path.Combine(fixture.Host.PluginAssetDirectory, "Models", model.Id);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, model.FileName), [4, 3, 2, 1]);
        Assert.False(plugin.LocalModels[0].Downloaded);
        var error = await Assert.ThrowsAsync<IOException>(() => plugin.LoadModelAsync(model.Id, default));
        Assert.Contains("integrity check", error.Message);
        Assert.False(plugin.IsAvailable);
        Assert.Null(plugin.SelectedModelId);
    }

    [Fact]
    public async Task DownloadModel_ReusesVerifiedCachedFileWithoutNetwork()
    {
        using var fixture = new PortableFixture();
        byte[] expected = [1, 2, 3, 4];
        var model = new LocalLlmModelDefinition("fixture", "Fixture", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        using var plugin = new LocalLlmPlugin([model]);
        await plugin.ActivateAsync(fixture.Host);
        var directory = Path.Combine(fixture.Host.PluginAssetDirectory, "Models", model.Id);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, model.FileName), expected);
        await plugin.DownloadModelAsync(model.Id, null, default);
        Assert.True(plugin.LocalModels[0].Downloaded);
    }

    [Fact]
    public async Task CancelledOperations_DoNotCreateModelFilesOrPublishAvailability()
    {
        using var fixture = new PortableFixture();
        using var plugin = new LocalLlmPlugin();
        await plugin.ActivateAsync(fixture.Host);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var id = plugin.LocalModels[0].Model.Id;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.DownloadModelAsync(id, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LoadModelAsync(id, cancelled.Token));
        Assert.False(plugin.IsAvailable);
        Assert.False(Directory.Exists(Path.Combine(fixture.Host.PluginAssetDirectory, "Models")));
    }

    [Fact]
    public async Task PartialFile_IsNotAdvertisedAsDownloaded_AndRemovalKeepsOtherFiles()
    {
        using var fixture = new PortableFixture();
        using var plugin = new LocalLlmPlugin();
        await plugin.ActivateAsync(fixture.Host);
        var model = plugin.ModelDefinitions[0];
        var directory = Path.Combine(fixture.Host.PluginAssetDirectory, "Models", model.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, model.FileName);
        await File.WriteAllTextAsync(path, "partial");
        var keep = Path.Combine(directory, "keep.txt"); await File.WriteAllTextAsync(keep, "keep");
        Assert.False(plugin.LocalModels[0].Downloaded);
        await Assert.ThrowsAsync<FileNotFoundException>(() => plugin.LoadModelAsync(model.Id, default));
        await plugin.RemoveModelAsync(model.Id, default);
        Assert.False(File.Exists(path)); Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task ThreadSetting_PersistsAndRejectsUnsupportedValues()
    {
        using var fixture = new PortableFixture();
        using var plugin = new LocalLlmPlugin();
        await plugin.ActivateAsync(fixture.Host);
        await plugin.SaveTextSettingAsync("threads", "4", default);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(fixture.Host);
        Assert.Equal("4", plugin.TextSettings.Single().Value);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("threads", "-1", default));
        Assert.Equal("4", plugin.TextSettings.Single().Value);
    }
}
