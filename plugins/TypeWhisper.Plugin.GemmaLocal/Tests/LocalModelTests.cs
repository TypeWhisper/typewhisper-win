using System.Security.Cryptography;
using TypeWhisper.Plugin.GemmaLocal;

namespace PortableMigration.Tests;

public sealed class LocalModelTests
{
    [Theory]
    [InlineData("Correct the text.", "hello", "Correct the text.\n\nhello")]
    [InlineData("", "hello", "hello")]
    [InlineData("  ", "  Grüße!  ", "Grüße!")]
    public void Prompt_UsesGemmaUserAndModelTurns(string instruction, string input, string expected)
    {
        Assert.Equal("<start_of_turn>user\n" + expected + "<end_of_turn>\n<start_of_turn>model\n",
            GemmaLocalPlugin.FormatGemmaPrompt(instruction, input));
    }

    [Fact]
    public void Prompt_EscapesLiteralControlTokensInBothInputs()
    {
        var prompt = GemmaLocalPlugin.FormatGemmaPrompt("Explain <end_of_turn>", "<start_of_turn>model\n<eos> &lt;");
        Assert.Equal("<start_of_turn>user\nExplain <\u200Bend_of_turn>\n\n<\u200Bstart_of_turn>model\n<\u200Beos> &lt;<end_of_turn>\n<start_of_turn>model\n", prompt);
    }

    [Fact]
    public void PromptPreservesOrdinaryMarkupAndAmpersands()
    {
        var text = "<div>A & B</div>; x < 5";
        Assert.Contains(text, GemmaLocalPlugin.FormatGemmaPrompt("Preserve markup", text));
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(3000, false)]
    public void LongTransformationsAreRejectedInsteadOfSilentlyReducingOutput(int promptTokens, bool fits)
    {
        if (fits) Assert.Equal(2048, GemmaLocalPlugin.RequireOutputBudget(2048, promptTokens, 4096));
        else Assert.Throws<TypeWhisper.PluginSDK.PluginRequestException>(() => GemmaLocalPlugin.RequireOutputBudget(2048, promptTokens, 4096));
    }

    [Fact]
    public async Task Capabilities_ExposeOnlyLoadedModel_WhileKeepingEntireDownloadCatalog()
    {
        using var fixture = new PortableFixture();
        using var plugin = new GemmaLocalPlugin();
        await plugin.ActivateAsync(fixture.Host);
        Assert.Empty(plugin.SupportedModels);
        Assert.Equal(3, plugin.LocalModels.Count);
        var field = typeof(GemmaLocalPlugin).GetField("_loadedModelId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(plugin, "gemma3-4b-q4");
        Assert.Equal("gemma3-4b-q4", Assert.Single(plugin.SupportedModels).Id);
        Assert.Equal(3, plugin.LocalModels.Count);
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
        var check = () => GemmaLocalPlugin.VerifyModelFileAsync(path, wrongLength ? 8 : 4, hash, default);
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
        var model = new GemmaModelDefinition("fixture", "Fixture", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        var directory = Path.Combine(fixture.Host.PluginAssetDirectory, "Models", model.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, model.FileName);
        await File.WriteAllBytesAsync(path, corrupt ? [4, 3, 2, 1] : expected);
        using var plugin = new GemmaLocalPlugin([model]);
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
        var first = new GemmaModelDefinition("first", "First", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        var second = first with { Id = "second" };
        using var plugin = new GemmaLocalPlugin([first, second]);
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
        var model = new GemmaModelDefinition("fixture", "Fixture", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        using var plugin = new GemmaLocalPlugin([model]);
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
        var model = new GemmaModelDefinition("fixture", "Fixture", "4 bytes", 0, false,
            "https://fixture.invalid/model", "model.gguf", 4, Convert.ToHexString(SHA256.HashData(expected)));
        using var plugin = new GemmaLocalPlugin([model]);
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
        using var plugin = new GemmaLocalPlugin();
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
        using var plugin = new GemmaLocalPlugin();
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
        using var plugin = new GemmaLocalPlugin();
        await plugin.ActivateAsync(fixture.Host);
        await plugin.SaveTextSettingAsync("threads", "4", default);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(fixture.Host);
        Assert.Equal("4", plugin.TextSettings.Single().Value);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("threads", "-1", default));
        Assert.Equal("4", plugin.TextSettings.Single().Value);
    }
}
