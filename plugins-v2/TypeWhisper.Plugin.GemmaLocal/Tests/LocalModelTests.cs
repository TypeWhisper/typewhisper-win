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
        Assert.Equal("<start_of_turn>user\nExplain &lt;end_of_turn>\n\n&lt;start_of_turn>model\n&lt;eos> &amp;lt;<end_of_turn>\n<start_of_turn>model\n", prompt);
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
