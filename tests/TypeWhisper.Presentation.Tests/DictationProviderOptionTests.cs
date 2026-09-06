using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictationProviderOptionTests
{
    [Theory]
    [InlineData(false, true, true, "Disabled")]
    [InlineData(true, false, true, "Setup required")]
    [InlineData(true, true, false, "Download required")]
    public void UnreadyProvidersCannotSupplyAnActiveModel(bool enabled, bool configured, bool downloaded, string status)
    {
        var provider = new DictationProviderOption("provider", "plugin", "Provider", enabled, configured, false,
            "model", [new("model", "Model", downloaded)]);
        Assert.False(provider.Ready);
        Assert.Null(provider.PreferredModelId);
        Assert.Equal(status, provider.Status);
    }

    [Fact]
    public void ProviderSwitchRestoresItsOwnReadyModel()
    {
        var provider = new DictationProviderOption("provider", "plugin", "Provider", true, true, false,
            "previous", [new("first", "First", true), new("previous", "Previous", true)]);
        Assert.Equal("previous", provider.PreferredModelId);
    }

    [Fact]
    public void MissingPreviousModelUsesReadyModelFromThatProvider()
    {
        var provider = new DictationProviderOption("provider", "plugin", "Provider", true, true, false,
            "missing", [new("missing", "Missing", false), new("downloaded", "Downloaded", true)]);
        Assert.Equal("downloaded", provider.PreferredModelId);
    }

    [Fact]
    public void EmptyCatalogNeedsSetupWithoutSelectingAnInventedModel()
    {
        var provider = new DictationProviderOption("future", "plugin", "Future", true, true, false, null, []);
        Assert.False(provider.Ready);
        Assert.Null(provider.PreferredModelId);
    }
}
