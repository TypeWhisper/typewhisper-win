using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class TermPackTests
{
    [Fact]
    public void AllPacks_Has13Packs()
    {
        Assert.Equal(13, TermPack.AllPacks.Length);
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
    [InlineData("legal")]
    [InlineData("finance")]
    [InlineData("music")]
    public void Pack_ExistsById(string id)
    {
        Assert.Contains(TermPack.AllPacks, p => p.Id == id);
    }
}
