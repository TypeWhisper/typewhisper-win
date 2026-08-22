using System.IO;
using System.Text.Json;
using TypeWhisper.Plugin.Claude;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.Plugin.GemmaLocal;
using TypeWhisper.Plugin.Groq;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.Plugin.OpenAiCompatible;
using TypeWhisper.Plugin.OpenRouter;
using TypeWhisper.Plugin.Xai;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WorkflowProviderReleaseMetadataTests
{
    public static TheoryData<string, string> Providers => new()
    {
        { "TypeWhisper.Plugin.Claude", "1.0.1" },
        { "TypeWhisper.Plugin.Gemini", "1.2.1" },
        { "TypeWhisper.Plugin.GemmaLocal", "1.0.1" },
        { "TypeWhisper.Plugin.Groq", "1.0.5" },
        { "TypeWhisper.Plugin.OpenAi", "1.1.2" },
        { "TypeWhisper.Plugin.OpenAiCompatible", "1.0.6" },
        { "TypeWhisper.Plugin.OpenRouter", "1.1.1" },
        { "TypeWhisper.Plugin.Xai", "1.0.1" },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void ReleaseMetadata_MatchesRuntimeAndRequiresCompatibleHost(
        string projectName,
        string expectedVersion)
    {
        var manifestPath = Path.Join(RepositoryRoot(), "plugins", projectName, "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var plugin = CreatePlugin(projectName);
        using var disposablePlugin = plugin as IDisposable;

        Assert.NotNull(manifest);
        Assert.Equal(expectedVersion, manifest.Version);
        Assert.Equal(expectedVersion, plugin.PluginVersion);
        Assert.Equal("1.0.9", manifest.MinHostVersion);
    }

    private static ITypeWhisperPlugin CreatePlugin(string projectName) => projectName switch
    {
        "TypeWhisper.Plugin.Claude" => new ClaudePlugin(),
        "TypeWhisper.Plugin.Gemini" => new GeminiPlugin(),
        "TypeWhisper.Plugin.GemmaLocal" => new GemmaLocalPlugin(),
        "TypeWhisper.Plugin.Groq" => new GroqPlugin(),
        "TypeWhisper.Plugin.OpenAi" => new OpenAiPlugin(),
        "TypeWhisper.Plugin.OpenAiCompatible" => new OpenAiCompatiblePlugin(),
        "TypeWhisper.Plugin.OpenRouter" => new OpenRouterPlugin(),
        "TypeWhisper.Plugin.Xai" => new XaiPlugin(),
        _ => throw new ArgumentOutOfRangeException(nameof(projectName), projectName, null),
    };

    private static string RepositoryRoot() => Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));
}
