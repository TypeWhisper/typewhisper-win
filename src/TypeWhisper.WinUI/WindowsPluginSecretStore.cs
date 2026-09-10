using System.Security.Cryptography;
using System.Text;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

// Separate from production credentials. Invalid ciphertext fails closed.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsPluginSecretStore(string directory) : IPluginSecretStore
{
    private static readonly byte[] Entropy = "TypeWhisper.WinUI.PluginSecrets.v1"u8.ToArray();
    private string SecretPath(string key) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".secret");
    public Task StoreAsync(string key, string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(directory);
            var path = SecretPath(key);
            File.WriteAllBytes(path + ".tmp", ciphertext);
            File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        return Task.CompletedTask;
    }
    public Task<string?> LoadAsync(string key)
    {
        var path = SecretPath(key);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        byte[] plaintext;
        try { plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return Task.FromResult<string?>(null); }
        try { return Task.FromResult<string?>(Encoding.UTF8.GetString(plaintext)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public Task DeleteAsync(string key) { File.Delete(SecretPath(key)); return Task.CompletedTask; }
}
