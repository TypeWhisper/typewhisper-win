using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowPaletteTests
{
    private static Workflow Workflow(string name, WorkflowTemplate template = WorkflowTemplate.CleanedText, WorkflowTrigger? trigger = null,
        bool enabled = true, int order = 0) => new()
        {
            Id = Guid.NewGuid().ToString(), Name = name, Template = template, Trigger = trigger ?? WorkflowTrigger.Manual(), IsEnabled = enabled,
            SortOrder = order, Behavior = new() { ProviderOverride = "provider", ModelOverride = "model" }
        };

    [Fact]
    public void OffersEnabledTextWorkflowsWhateverTheirTrigger()
    {
        var items = WorkflowPalette.Candidates(
        [
            Workflow("Translate", WorkflowTemplate.Translation, order: 2),
            Workflow("Email", WorkflowTemplate.EmailReply, WorkflowTrigger.App("outlook"), order: 1),
            Workflow("Disabled", enabled: false),
            Workflow("Plain dictation", WorkflowTemplate.Dictation)
        ]);
        Assert.Equal(["Email", "Translate"], items.Select(item => item.Workflow.Name));
        Assert.Equal("Email Reply · outlook", items[0].Subtitle);
        Assert.Equal("Translation · Manual", items[1].Subtitle);
    }

    [Fact]
    public void FiltersByNameAndSubtitle()
    {
        var items = WorkflowPalette.Candidates([Workflow("Translate", WorkflowTemplate.Translation), Workflow("Tidy up")]);
        Assert.Equal(["Translate"], WorkflowPalette.Filter(items, "trans").Select(item => item.Workflow.Name));
        Assert.Equal(["Tidy up"], WorkflowPalette.Filter(items, "cleaned").Select(item => item.Workflow.Name));
        Assert.Equal(2, WorkflowPalette.Filter(items, " ").Count);
    }

    [Theory]
    [InlineData(-1, 1, 3, 0)]
    [InlineData(-1, -1, 3, 2)]
    [InlineData(2, 1, 3, 0)]
    [InlineData(0, -1, 3, 2)]
    [InlineData(1, 1, 3, 2)]
    [InlineData(0, 1, 0, -1)]
    public void SelectionWrapsAtBothEnds(int index, int offset, int count, int expected) =>
        Assert.Equal(expected, WorkflowPalette.Move(index, offset, count));
}
