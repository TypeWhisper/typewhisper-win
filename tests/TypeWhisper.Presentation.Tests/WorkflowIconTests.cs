using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public sealed class WorkflowIconTests
{
    [Fact]
    public void AllThirtyIconsSurviveEditorAndJsonRoundTrip()
    {
        Assert.Equal(30, WorkflowIcons.All.Count);
        Assert.Equal(30, WorkflowIcons.All.Select(icon => icon.Id).Distinct().Count());
        foreach (var (icon, _) in WorkflowIcons.All)
        {
            var workflow = new Workflow { Id = "icon-test", Name = "Icon test", Icon = icon, Template = WorkflowTemplate.Custom, Trigger = WorkflowTrigger.Manual() };
            var stored = WorkflowDraft.FromStored(workflow).ToStored();
            var restored = JsonSerializer.Deserialize<Workflow>(JsonSerializer.Serialize(stored))!;
            Assert.Equal(icon, WorkflowDraft.FromStored(restored).IconKind);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void MissingOrUnknownIconsUseWorkflowSymbol(string? value) => Assert.Equal("workflow", WorkflowIcons.Normalize(value));
}
