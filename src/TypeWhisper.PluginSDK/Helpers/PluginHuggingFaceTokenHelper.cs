using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Provides shared normalization, secure persistence, and validation for optional
/// Hugging Face download credentials. Host secret storage remains plugin-scoped.
/// </summary>
public static class PluginHuggingFaceTokenHelper
{
    /// <summary>Stable plugin-scoped secret key used by TypeWhisper plugins.</summary>
    public const string StorageKey = "hugging-face-token";

    private static readonly Uri ValidationUri = new("https://huggingface.co/api/whoami-v2");
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>Trims a token and rejects embedded whitespace.</summary>
    public static string? NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim();
        if (normalized.Any(char.IsWhiteSpace))
            throw new ArgumentException("Hugging Face tokens cannot contain whitespace.", nameof(token));

        return normalized;
    }

    /// <summary>Loads the current plugin-scoped token.</summary>
    public static async Task<string?> LoadTokenAsync(IPluginHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return NormalizeToken(await host.LoadSecretAsync(StorageKey));
    }

    /// <summary>Stores a normalized plugin-scoped token and returns it.</summary>
    public static async Task<string?> SaveTokenAsync(IPluginHostServices host, string? token)
    {
        ArgumentNullException.ThrowIfNull(host);
        var normalized = NormalizeToken(token);
        if (normalized is null)
            await host.DeleteSecretAsync(StorageKey);
        else
            await host.StoreSecretAsync(StorageKey, normalized);

        return normalized;
    }

    /// <summary>Removes the current plugin-scoped token.</summary>
    public static Task ClearTokenAsync(IPluginHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.DeleteSecretAsync(StorageKey);
    }

    /// <summary>
    /// Validates a token against the Hugging Face identity endpoint. No exception
    /// details or secret values are returned to the caller.
    /// </summary>
    public static async Task<bool> ValidateTokenAsync(
        string token,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        string? normalized;
        try
        {
            normalized = NormalizeToken(token);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (normalized is null)
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, ValidationUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalized);

        try
        {
            using var response = await (httpClient ?? SharedHttpClient)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            return document.RootElement.TryGetProperty("name", out _)
                || document.RootElement.TryGetProperty("type", out _)
                || document.RootElement.TryGetProperty("auth", out _);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or IOException)
        {
            return false;
        }
    }
}
