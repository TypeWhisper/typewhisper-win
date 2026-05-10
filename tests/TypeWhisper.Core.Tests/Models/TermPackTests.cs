using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class TermPackTests
{
    [Fact]
    public void AllPacks_Has12Packs()
    {
        Assert.Equal(12, TermPack.AllPacks.Length);
    }

    [Fact]
    public void AllPacks_HaveUniqueIds()
    {
        var ids = TermPack.AllPacks.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AllPacks_HaveTerms()
    {
        Assert.All(TermPack.AllPacks, p =>
        {
            Assert.NotEmpty(p.Terms);
            Assert.NotEmpty(p.Name);
            Assert.NotEmpty(p.Icon);
        });
    }

    [Theory]
    [InlineData("web-dev")]
    [InlineData("dotnet")]
    [InlineData("devops")]
    [InlineData("data-ai")]
    [InlineData("design")]
    [InlineData("gamedev")]
    [InlineData("mobile")]
    [InlineData("security")]
    [InlineData("databases")]
    [InlineData("medical")]
    [InlineData("finance")]
    [InlineData("music")]
    public void Pack_ExistsById(string id)
    {
        Assert.Contains(TermPack.AllPacks, p => p.Id == id);
    }

    [Fact]
    public void IndustryPackIds_AreReservedForRemoteCatalog()
    {
        Assert.Contains("real-estate", TermPack.IndustryPackIds);
        Assert.Contains("architecture", TermPack.IndustryPackIds);
        Assert.Contains("legal", TermPack.IndustryPackIds);

        Assert.DoesNotContain(TermPack.AllPacks, pack => TermPack.IndustryPackIds.Contains(pack.Id));
    }

    [Fact]
    public void VisiblePacks_DoesNotIncludeRemoteIndustryPacksWithoutCommercialLicense()
    {
        var visibleIds = TermPack.VisiblePacks(hasCommercialLicense: false).Select(pack => pack.Id).ToHashSet();

        Assert.DoesNotContain("real-estate", visibleIds);
        Assert.DoesNotContain("architecture", visibleIds);
        Assert.DoesNotContain("legal", visibleIds);
    }

    [Fact]
    public void VisiblePacks_DoesNotIncludeRemoteIndustryPacksWithCommercialLicense()
    {
        var visibleIds = TermPack.VisiblePacks(hasCommercialLicense: true).Select(pack => pack.Id).ToHashSet();

        Assert.DoesNotContain("real-estate", visibleIds);
        Assert.DoesNotContain("architecture", visibleIds);
        Assert.DoesNotContain("legal", visibleIds);
    }
}
