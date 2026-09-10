using System.Text;
using TypeWhisper.WinUI;
using Xunit;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsPluginSecretStoreTests
{
    [Fact]
    public async Task SecretsAreEncryptedRoundTripAndCanBeReplacedAfterCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "plugin-secrets-" + Guid.NewGuid());
        try
        {
            var store = new WindowsPluginSecretStore(root);
            Assert.Null(await store.LoadAsync("api-key"));
            await store.StoreAsync("api-key", "synthetic-secret-for-test");
            var path = Assert.Single(Directory.GetFiles(root));
            Assert.DoesNotContain("synthetic-secret-for-test", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            var restarted = new WindowsPluginSecretStore(root);
            Assert.Equal("synthetic-secret-for-test", await restarted.LoadAsync("api-key"));
            File.WriteAllText(path, "corrupted-or-plaintext-secret");
            Assert.Null(await restarted.LoadAsync("api-key"));
            await restarted.StoreAsync("api-key", "replacement");
            Assert.Equal("replacement", await restarted.LoadAsync("api-key"));
            await restarted.DeleteAsync("api-key");
            Assert.Null(await restarted.LoadAsync("api-key")); Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
