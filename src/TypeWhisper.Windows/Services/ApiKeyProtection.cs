using System.Security.Cryptography;
using System.Text;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Encrypts/decrypts API keys using Windows DPAPI (current user scope).
/// Encrypted data can only be decrypted by the same Windows user on the same machine.
/// </summary>
public static class ApiKeyProtection
{
    private static readonly byte[] Entropy = "TypeWhisper.ApiKey.v1"u8.ToArray();

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        try
        {
            var bytes = Convert.FromBase64String(encrypted);
            var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            // Key was stored in plaintext (migration from old version) — return as-is
            return encrypted;
        }
        catch (FormatException)
        {
            // Not Base64 — likely plaintext from old version
            return encrypted;
        }
    }
}
