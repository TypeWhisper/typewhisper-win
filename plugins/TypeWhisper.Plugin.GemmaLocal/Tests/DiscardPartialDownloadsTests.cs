using System.Security.Cryptography;
using TypeWhisper.Plugin.GemmaLocal;

namespace PortableMigration.Tests;

public sealed class DiscardPartialDownloadsTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly GemmaModelDefinition Model = new("fixture", "Fixture", "8 bytes", 0, false,
        "https://fixture.invalid/model", "model.gguf", Payload.Length, Convert.ToHexString(SHA256.HashData(Payload)));

    [Fact]
    public async Task DiscardActionRemovesOnlyKnownPartialsAndHonorsCancellation()
    {
        using var f = new PortableFixture();
        using var plugin = new GemmaLocalPlugin([Model]);
        await plugin.ActivateAsync(f.Host);
        var directory = Path.Combine(f.Host.PluginAssetDirectory, "Models", Model.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Model.FileName);
        await File.WriteAllBytesAsync(path, Payload);
        await File.WriteAllTextAsync(path + ".download", "partial");
        await File.WriteAllTextAsync(path + ".download.tmp", "old partial");
        await File.WriteAllTextAsync(Path.Combine(directory, "foreign.download"), "keep");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ExecuteSettingsActionAsync("discard-partial-downloads", canceled.Token));
        Assert.True(File.Exists(path + ".download"));
        await plugin.ExecuteSettingsActionAsync("discard-partial-downloads", default);
        Assert.False(File.Exists(path + ".download"));
        Assert.False(File.Exists(path + ".download.tmp"));
        Assert.True(File.Exists(Path.Combine(directory, "foreign.download")));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }
}
