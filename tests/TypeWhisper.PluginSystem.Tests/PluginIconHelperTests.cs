using System.IO;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginIconHelperTests : IDisposable
{
    private readonly string _baseDirectory;

    public PluginIconHelperTests()
    {
        _baseDirectory = Path.Join(Path.GetTempPath(), $"typewhisper-plugin-icons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(BuildLogoDirectory());
    }

    [Fact]
    public void GetLogoPath_KnownPluginWithAsset_ReturnsLocalAssetPath()
    {
        var expectedPath = BuildLogoPath("openai.png");
        WriteValidPng(expectedPath);

        var path = PluginIconHelper.GetLogoPath("com.typewhisper.openai", _baseDirectory);

        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void GetLogoPath_KnownPluginWithoutAsset_ReturnsNullForFallback()
    {
        var path = PluginIconHelper.GetLogoPath("com.typewhisper.groq", _baseDirectory);

        Assert.Null(path);
        Assert.Equal("\U0001F4A8", PluginIconHelper.GetIcon("com.typewhisper.groq"));
    }

    [Fact]
    public void GetLogoPath_DoesNotUseOpenAiLogoForCompatibleProviders()
    {
        var expectedPath = BuildLogoPath("openai.png");
        WriteValidPng(expectedPath);

        var path = PluginIconHelper.GetLogoPath("com.typewhisper.openai-compatible", _baseDirectory);

        Assert.Null(path);
        Assert.Equal("\U0001F310", PluginIconHelper.GetIcon("com.typewhisper.openai-compatible"));
    }

    [Fact]
    public void GetLogoPath_SmallestAi_ReturnsProviderLogoWithFallback()
    {
        var expectedPath = BuildLogoPath("smallest.png");
        WriteValidPng(expectedPath);

        Assert.Equal(expectedPath, PluginIconHelper.GetLogoPath("com.typewhisper.smallest-ai", _baseDirectory));
        Assert.Equal("\U0001F399", PluginIconHelper.GetIcon("com.typewhisper.smallest-ai"));
        Assert.Equal("#14B8A6", PluginIconHelper.GetGradientStart("com.typewhisper.smallest-ai"));
        Assert.Equal("#2563EB", PluginIconHelper.GetGradientEnd("com.typewhisper.smallest-ai"));
    }

    [Theory]
    [InlineData("com.typewhisper.openai", "openai.png")]
    [InlineData("com.typewhisper.groq", "groq.png")]
    [InlineData("com.typewhisper.xai", "xai.png")]
    [InlineData("com.typewhisper.gemini", "gemini.png")]
    [InlineData("com.typewhisper.claude", "claude.png")]
    [InlineData("com.typewhisper.cohere", "cohere.png")]
    [InlineData("com.typewhisper.smallest-ai", "smallest.png")]
    public void GetLogoPath_MapsApprovedSvglPlugins(string pluginId, string fileName)
    {
        var expectedPath = BuildLogoPath(fileName);
        WriteValidPng(expectedPath);

        var path = PluginIconHelper.GetLogoPath(pluginId, _baseDirectory);

        Assert.Equal(expectedPath, path);
    }

    private static void WriteValidPng(string path)
    {
        const string onePixelPng =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=";
        File.WriteAllBytes(path, Convert.FromBase64String(onePixelPng));
    }

    private string BuildLogoDirectory() =>
        Path.Join(_baseDirectory, "Resources", "PluginLogos");

    private string BuildLogoPath(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        return Path.Join(BuildLogoDirectory(), safeFileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
            Directory.Delete(_baseDirectory, recursive: true);
    }
}
